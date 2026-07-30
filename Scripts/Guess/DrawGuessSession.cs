using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.GameActions;
using DrawAndGuessMod.Scripts.Networking;
using DrawAndGuessMod.Scripts.Ui;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;

namespace DrawAndGuessMod.Scripts.Guess;

/// <summary>
/// "你画我猜"模式的绘画者流程（Player A）。
///
/// 架构要点：本流程只在出牌者本人所在端执行（出牌者即绘画者，无接管协商——
/// postfix 在 async 方法返回 Task 时同步触发，此刻前台流程才刚进入第一个 await，
/// 任何基于"接管标记"的判断都必然读到过期的状态）。
/// 其余客户端的 DrawGuessBlank.OnPlay 立即返回，观看流程由 DrawGuessSpectatorPatch 在
/// 后台独立运行；发牌由绘画者端入队 DrawGuessGrantAction 经行动队列同步到全房。
/// </summary>
internal static class DrawGuessSession
{
    /// <summary>绘画者确认画作后，猜测环节的默认时长（秒）。</summary>
    public const double GuessTimeoutSeconds = 45d;

    /// <summary>
    /// 从 PlayerChoiceContext 提取会话 Id。出牌时游戏传入的是 BranchingPlayerChoiceContext，
    /// 其 _originalContext 字段才持有 GameActionPlayerChoiceContext.Action；
    /// 取不到时统一回退 0（各端行为一致即可）。
    /// </summary>
    public static uint GetSessionId(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext choiceContext)
    {
        try
        {
            object context = choiceContext;
            if (choiceContext.GetType().Name == "BranchingPlayerChoiceContext")
            {
                FieldInfo? original = choiceContext.GetType().GetField("_originalContext", BindingFlags.NonPublic | BindingFlags.Instance);
                context = original?.GetValue(choiceContext) ?? choiceContext;
            }

            if (context is MegaCrit.Sts2.Core.GameActions.Multiplayer.GameActionPlayerChoiceContext actionContext)
            {
                return actionContext.Action.Id ?? 0u;
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Debug($"[DrawAndGuessMod] 无法从选择上下文提取会话 Id: {ex.Message}");
        }

        return 0u;
    }

    // ---------------------------------------------------------------- 绘画者（Player A）

    /// <summary>前台绘画者流程：本地作画 → 广播 → 聚合猜测 → 裁定（发牌见 <see cref="FinishWithResultAsync"/>）。</summary>
    public static async Task RunOwnerAsync(Player owner, uint sessionId, bool isUpgraded)
    {
        // 阶段一：本地作画（不广播过程，彻底本地优先）。
        DrawingResult? drawing = await DrawingScreen.ShowSoloAsync(owner, sessionId);
        byte[]? png = drawing?.PngBytes;
        if (png == null || png.Length == 0)
        {
            // 绘画者取消：广播取消结果，释放所有观看端的等待，避免整局卡死。
            Entry.Logger.Info("[DrawAndGuessMod] 绘画者取消了作画。");
            DrawingNetSync.SendDrawGuessResult(new DrawGuessResultMessage
            {
                OwnerId = owner.NetId,
                SessionId = sessionId,
                Cancelled = true,
                CardId = string.Empty,
                TotalGuesses = 0
            });
            await WaitForOutcomeEchoAsync(owner.NetId, sessionId);
            return;
        }

        // 阶段二：广播开始包前先等一帧——确保各观看端的行动队列已执行到这张卡
        // （观看端补丁在队列内点火），否则开始包会先于观看端挂起等待而到达。
        if (Engine.GetMainLoop() is SceneTree tree0)
        {
            await tree0.ToSignal(tree0, SceneTree.SignalName.ProcessFrame);
        }

        // 阶段三：剥离 AI 识别，广播 StartGuessingPacket 并进入等待接收状态。
        ulong[] expectedGuessers = owner.RunState.Players
            .Where(player => player.NetId != owner.NetId)
            .Select(player => player.NetId)
            .ToArray();

        DrawGuessWaitingOverlay overlay = new();
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            tree.Root.AddChild(overlay);
        }

        overlay.Bind(owner.NetId, sessionId, expectedGuessers.Length, png);

        DrawGuessOutcome? outcome;
        try
        {
            outcome = await GuessPhaseCoordinator.BeginOwnerSession(
                owner,
                sessionId,
                png,
                expectedGuessers,
                GuessTimeoutSeconds,
                overlay.SetProgress);
            Entry.Logger.Info($"[DrawAndGuessMod][Trace] 绘画者端结算完成: outcome={(outcome == null ? "null" : outcome.CardId)}");
        }
        catch (TaskCanceledException)
        {
            outcome = null;
        }
        finally
        {
            overlay.QueueFree();
        }

        if (outcome == null)
        {
            return;
        }

        // 阶段四：发牌——作为同步 GameAction 经行动队列广播，各端在同一行动序列中
        // 执行并各自生成校验和（已验证：这是联机下发牌唯一不触发状态分歧的方式）。
        await WaitForOutcomeEchoAsync(owner.NetId, sessionId);
        DrawGuessGrantAction.EnqueueGrant(owner, outcome.CardId, isUpgraded);
        await DrawGuessSpectator.ApplyArtworkOnlyAsync(owner, outcome);
        Entry.Logger.Info($"[DrawAndGuessMod][Trace] 绘画者端发牌已请求: {outcome.CardId}");
    }

    /// <summary>等本端"看到"自己广播的裁定（会话在本地被标记完成）再返回。</summary>
    private static async Task WaitForOutcomeEchoAsync(ulong ownerId, uint sessionId)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        // 裁定广播由本端网络层立即应用到本地会话；这里主要起到"让出一帧"的作用，
        // 保证观看端有机会在 checksum 之前完成结算。
        for (int i = 0; i < 3; i++)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }
}
