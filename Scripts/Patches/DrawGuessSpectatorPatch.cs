using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Cards;
using DrawAndGuessMod.Scripts.Guess;
using DrawAndGuessMod.Scripts.Networking;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace DrawAndGuessMod.Scripts.Patches;

/// <summary>
/// “你画我猜”观看端重放补丁。
///
/// 关键点（经 Harmony 2.4.2 实测）：对 async 方法的同步 postfix 在方法**返回 Task 的那一刻**
/// 就会触发——此刻 OnPlay 的前台流程才刚进入第一个 await。因此任何依赖运行时状态
/// （如“本端是否正在前台作画”）的判断都不可靠，这里用纯确定性规则：
/// 出牌者本人所在端 = 绘画者（前台流程在 OnPlay 本体内执行），其余端 = 观看端。
///
/// 观看端流程（猜测 UI + 画作贴图）完全独立于行动队列运行；
/// 发牌由绘画者端入队 DrawGuessGrantAction 经行动队列同步到本端，本端不做本地结算。
/// </summary>
[HarmonyPatch(typeof(DrawGuessBlank), "OnPlay")]
internal static class DrawGuessSpectatorPatch
{
    private static void Postfix(DrawGuessBlank __instance, PlayerChoiceContext choiceContext)
    {
        if (!DrawingNetSync.IsMultiplayer)
        {
            return;
        }

        if (LocalContext.IsMe(__instance.Owner))
        {
            // 出牌者所在端：前台绘画流程已在 OnPlay 本体内执行，此处无需再点。
            return;
        }

        uint sessionId = DrawGuessSession.GetSessionId(choiceContext);
        Entry.Logger.Info($"[DrawAndGuessMod][Trace] 观看端流程点火: owner={__instance.Owner.NetId}, session={sessionId}, local={LocalContext.NetId}");

        // 点火：观看端流程独立于行动队列运行。
        _ = RunSpectatorFlowAsync(__instance, sessionId);
    }

    private static async Task RunSpectatorFlowAsync(DrawGuessBlank card, uint sessionId)
    {
        Entry.Logger.Info($"[DrawAndGuessMod][Trace] 观看端后台循环启动: owner={card.Owner.NetId}, session={sessionId}");
        DrawGuessOutcome? outcome = await DrawGuessSpectatorLoop.RunAsync(card.Owner, card.Owner.NetId, sessionId);
        Entry.Logger.Info($"[DrawAndGuessMod][Trace] 观看端流程返回: outcome={(outcome == null ? "null" : outcome.CardId)}");
        if (outcome == null)
        {
            return;
        }

        // 发牌由绘画者端入队的 DrawGuessGrantAction 经行动队列同步到本端（与绘画者端
        // 在同一行动序列位置执行、生成同一校验上下文）；本端只保存画作贴图用于卡面展示。
        await DrawGuessSpectator.ApplyArtworkOnlyAsync(card.Owner, outcome);
        Entry.Logger.Info($"[DrawAndGuessMod][Trace] 观看端画作贴图完成: {outcome.CardId}");
    }
}
