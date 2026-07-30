using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Localization;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Guess;

/// <summary>
/// 观看端后台循环：独立于行动队列运行，负责猜测 UI 的生命周期。
///
/// 为什么需要它：sts2 联机模式下网络消息在传输线程分发、且没有
/// SynchronizationContext 编组——网络处理器里完成的 TaskCompletionSource，
/// 其 await 续体可能永远丢失（线程池在不安全的时机内联执行）。
/// 因此观看端改为"后台任务 + 轮询会话快照"：不 await 任何会被网络线程
/// 完成的 TCS，只在本任务里同步读取协调器暴露的快照。
/// </summary>
internal static class DrawGuessSpectatorLoop
{
    /// <summary>
    /// 检查当前会话是否仍然有效：绘画者仍在线、Run 未放弃/结束、Godot 节点树仍存活。
    /// </summary>
    private static bool IsSessionAlive(Player owner, SceneTree tree)
    {
        if (!GodotObject.IsInstanceValid(tree))
        {
            return false;
        }

        try
        {
            RunManager? runManager = RunManager.Instance;
            if (runManager == null)
            {
                return false;
            }

            // 游戏已放弃、正在清理或不在进行中时视为失效。
            if (runManager.IsAbandoned || runManager.IsCleaningUp || !runManager.IsInProgress)
            {
                return false;
            }

            // 绘画者已从 RunLobby 的连接玩家列表中移除（断线时立即更新）。
            try
            {
                var lobby = runManager.RunLobby;
                if (lobby != null)
                {
                    var connectedIds = lobby.ConnectedPlayerIds;
                    if (connectedIds != null && !connectedIds.Contains(owner.NetId))
                    {
                        return false;
                    }
                }
            }
            catch { }
        }
        catch
        {
            // 访问 RunManager 失败时保守认为已失效。
            return false;
        }

        return true;
    }

    /// <summary>
    /// 观看端完整流程：等开始 → 弹猜测 UI → 提交 → 等裁定。
    /// 返回最终裁定；绘画者取消、断线或会话无效时返回 null。
    /// </summary>
    public static async Task<DrawGuessOutcome?> RunAsync(Player owner, ulong ownerId, uint sessionId)
    {
        // 等开始包（会话快照由网络处理器同步填充，这里只是轮询读取）。
        byte[]? png = await WaitForStartSnapshotAsync(owner, ownerId, sessionId);
        Entry.Logger.Info($"[DrawAndGuessMod][Trace] 后台循环开始包: png={(png == null ? "null" : png.Length.ToString())}");
        if (png == null || png.Length == 0)
        {
            return null;
        }

        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return null;
        }

        // 弹猜测 UI（主线程）。
        Ui.GuessEntryOverlay? overlay = null;
        try
        {
            overlay = new Ui.GuessEntryOverlay();
            tree.Root.AddChild(overlay);
            overlay.BindPolling(owner, GuessPhaseCoordinator.GetSessionTimeout(ownerId, sessionId));
            overlay.SetImage(png);

            double deadline = GuessPhaseCoordinator.GetSessionTimeout(ownerId, sessionId);
            double hardTimeout = deadline + 10d; // 绝对超时兜底：多给绘画者10秒网络延迟，还不发裁定就强行认为其掉线。
            bool submitted = false;

            while (true)
            {
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                double delta = tree.Root.GetProcessDeltaTime();
                deadline -= delta;
                hardTimeout -= delta;

                // 会话被裁定/取消 → 直接收尾（无需等待本地 UI）。
                if (GuessPhaseCoordinator.TryGetOutcomeSnapshot(ownerId, sessionId, out DrawGuessOutcome? outcome))
                {
                    Entry.Logger.Info($"[DrawAndGuessMod][Trace] 后台循环收到裁定: {(outcome == null ? "null(取消)" : outcome.CardId)}");
                    return outcome;
                }

                // 绘画者断线或 Run 已结束 → 关闭 UI 并退出，避免永久卡住。
                if (!IsSessionAlive(owner, tree))
                {
                    Entry.Logger.Info("[DrawAndGuessMod] 会话失效（绘画者离线或 Run 结束），关闭猜测 UI。");
                    return null;
                }

                // 硬超时：绘画者断线导致永远收不到网络包时，强制退出
                if (hardTimeout <= 0d)
                {
                    Entry.Logger.Warn("[DrawAndGuessMod] 等待最终裁定超时（绘画者可能已掉线），强制关闭猜测 UI。");
                    return null;
                }

                if (!submitted && overlay.TakePendingGuess() is { } cardId)
                {
                    submitted = true;
                    GuessPhaseCoordinator.SubmitGuess(ownerId, sessionId, cardId);
                    overlay.LockToWaiting(cardId.Length == 0
                        ? ModText.Get("已放弃猜测，等待结算……", "Skipped, waiting for result...")
                        : ModText.Get("已提交猜测，等待结算……", "Guess submitted, waiting for result..."));
                }

                // 本地倒计时兜底：到点也走提交/弃权流程（权威仍在绘画者端）。
                if (!submitted && deadline <= 0d)
                {
                    submitted = true;
                    GuessPhaseCoordinator.SubmitGuess(ownerId, sessionId, overlay.CurrentBestCardId);
                    overlay.LockToWaiting(ModText.Get("时间到，等待结算……", "Time's up, waiting for result..."));
                }
            }
        }
        finally
        {
            overlay?.QueueFree();
            // 客机应用完结果后主动删除会话，释放 PNG 和 TCS 占用的内存。
            GuessPhaseCoordinator.RemoveSession(ownerId, sessionId);
        }
    }

    private static async Task<byte[]?> WaitForStartSnapshotAsync(Player owner, ulong ownerId, uint sessionId)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return null;
        }

        while (true)
        {
            // 会话已结束（取消/裁定先于开始到达）。
            if (GuessPhaseCoordinator.IsCompleted(ownerId, sessionId))
            {
                return null;
            }

            // 绘画者断线或 Run 已结束：停止等待，避免永远卡在此处。
            if (!IsSessionAlive(owner, tree))
            {
                Entry.Logger.Info("[DrawAndGuessMod] 等待开始包时会话失效（绘画者离线或 Run 结束）。");
                return null;
            }

            if (GuessPhaseCoordinator.TryGetStartSnapshot(ownerId, sessionId, out byte[]? png))
            {
                return png;
            }

            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }
}
