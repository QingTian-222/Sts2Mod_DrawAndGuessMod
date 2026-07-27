using System;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;

namespace DrawAndGuessMod.Scripts.Guess;

/// <summary>
/// 观看端后台循环：独立于行动队列运行，负责猜测 UI 的生命周期。
///
/// 为什么需要它：sts2 联机模式下网络消息在传输线程分发、且没有
/// SynchronizationContext 编组——网络处理器里完成的 TaskCompletionSource，
/// 其 await 续体可能永远丢失（线程池在不安全的时机内联执行）。
/// 因此观看端改为“后台任务 + 轮询会话快照”：不 await 任何会被网络线程
/// 完成的 TCS，只在本任务里同步读取协调器暴露的快照。
/// </summary>
internal static class DrawGuessSpectatorLoop
{
    /// <summary>
    /// 观看端完整流程：等开始 → 弹猜测 UI → 提交 → 等裁定。
    /// 返回最终裁定；绘画者取消或会话无效时返回 null。
    /// </summary>
    public static async Task<DrawGuessOutcome?> RunAsync(Player owner, ulong ownerId, uint sessionId)
    {
        // 等开始包（会话快照由网络处理器同步填充，这里只是轮询读取）。
        byte[]? png = await WaitForStartSnapshotAsync(ownerId, sessionId);
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
            bool submitted = false;

            while (true)
            {
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

                // 会话被裁定/取消 → 直接收尾（无需等待本地 UI）。
                if (GuessPhaseCoordinator.TryGetOutcomeSnapshot(ownerId, sessionId, out DrawGuessOutcome? outcome))
                {
                    Entry.Logger.Info($"[DrawAndGuessMod][Trace] 后台循环收到裁定: {(outcome == null ? "null(取消)" : outcome.CardId)}");
                    return outcome;
                }

                if (!submitted && overlay.TakePendingGuess() is { } cardId)
                {
                    submitted = true;
                    GuessPhaseCoordinator.SubmitGuess(ownerId, sessionId, cardId);
                    overlay.LockToWaiting(cardId.Length == 0 ? "已放弃猜测，等待结算……" : "已提交猜测，等待结算……");
                }

                // 本地倒计时兜底：到点也走提交/弃权流程（权威仍在绘画者端）。
                deadline -= tree.Root.GetProcessDeltaTime();
                if (!submitted && deadline <= 0d)
                {
                    submitted = true;
                    GuessPhaseCoordinator.SubmitGuess(ownerId, sessionId, overlay.CurrentBestCardId);
                    overlay.LockToWaiting("时间到，等待结算……");
                }
            }
        }
        finally
        {
            overlay?.QueueFree();
        }
    }

    private static async Task<byte[]?> WaitForStartSnapshotAsync(ulong ownerId, uint sessionId)
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

            if (GuessPhaseCoordinator.TryGetStartSnapshot(ownerId, sessionId, out byte[]? png))
            {
                return png;
            }

            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }
}
