using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Networking;
using DrawAndGuessMod.Scripts.Ui;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Guess;

/// <summary>
/// 观看端流程：房主先行、其余玩家在后台队列执行时，经由
/// <see cref="DrawGuessSpectatorPatch"/> 逐个延后重放。
/// 当裁定到达时，最近一个被阻塞的 await 正好持有同一组数据，直接返回即可。
/// </summary>
internal static class DrawGuessSpectator
{
    /// <summary>重放进门：返回最终裁定；房主端返回 null（房主走前台流程）。</summary>
    public static async Task<DrawGuessOutcome?> RunAsync(Player owner, uint sessionId)
    {
        Entry.Logger.Info($"[DrawAndGuessMod][Trace] 观看端等待开始包: owner={owner.NetId}, session={sessionId}, local={LocalContext.NetId}");

        byte[]? png = await GuessPhaseCoordinator.AwaitStartAsync(owner.NetId, sessionId);
        Entry.Logger.Info($"[DrawAndGuessMod][Trace] 观看端收到开始包: png={(png == null ? "null" : png.Length.ToString())}");
        if (png == null || png.Length == 0)
        {
            return null;
        }

        GuessEntryOverlay overlay = new();
        if (Godot.Engine.GetMainLoop() is Godot.SceneTree tree)
        {
            tree.Root.AddChild(overlay);
        }

        overlay.Bind(owner, sessionId, png, GuessPhaseCoordinator.GetSessionTimeout(owner.NetId, sessionId));

        Task submitWait = overlay.WaitForSubmitAsync();
        DrawGuessOutcome? outcome = await GuessPhaseCoordinator.AwaitResultAsync(owner.NetId, sessionId);
        Entry.Logger.Info($"[DrawAndGuessMod][Trace] 观看端裁定等待返回: outcome={(outcome == null ? "null" : outcome.CardId)}");
        overlay.QueueFree();
        await submitWait;
        return outcome;
    }

    /// <summary>
    /// 观看端结算：本地仅记录画作贴图（用于卡面补丁展示），
    /// 卡牌实体由绘画者端通过原生管线发放并同步，本端不触碰牌堆，避免校验分歧。
    /// </summary>
    public static Task ApplyArtworkOnlyAsync(Player owner, DrawGuessOutcome outcome)
    {
        try
        {
            State.ArtworkStore.Set(owner.RunState, outcome.CardId, outcome.PngBytes);
            Entry.Logger.Info($"[DrawAndGuessMod] 观看端已应用画作贴图: {outcome.CardId}");
        }
        catch (System.Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] 观看端应用画作贴图失败: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>结算与展示：创建最终卡牌、加入牌库并复制到手牌；检视界面只在绘画者端打开。</summary>
    public static async Task ResolveAndPresentAsync(Player owner, DrawGuessOutcome outcome, bool isUpgraded, bool openInspectScreen)
    {
        CardModel? template = ModelDb.AllCards.FirstOrDefault(card =>
            string.Equals(card.Id.Entry, outcome.CardId, System.StringComparison.Ordinal));
        if (template == null)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] 裁定卡牌 {outcome.CardId} 在本端卡牌库中不存在。");
            return;
        }

        CardModel selectedCard = owner.RunState.CreateCard(template, owner);
        if (isUpgraded && selectedCard.IsUpgradable)
        {
            CardCmd.Upgrade(selectedCard);
        }

        // 各端保存画作贴图（补丁钩子会让这张卡显示玩家画作）。
        State.ArtworkStore.Set(owner.RunState, selectedCard.Id.Entry, outcome.PngBytes);

        // 原生管线：加入牌库 + 复制到手牌，联机端自动同步。
        MegaCrit.Sts2.Core.Combat.ICombatState? combatState = owner.Creature.CombatState;
        if (combatState == null)
        {
            Entry.Logger.Warn("[DrawAndGuessMod] Combat ended before the guessed card could be created.");
            return;
        }

        CardPileAddResult deckResult = await CardPileCmd.Add(selectedCard, MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck);
        if (!deckResult.success)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to add selected card {selectedCard.Id.Entry} to deck.");
            return;
        }

        CardModel handCard = combatState.CloneCard(deckResult.cardAdded);
        handCard.DeckVersion = deckResult.cardAdded;
        await CardPileCmd.Add(handCard, MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand);
        CardCmd.PreviewCardPileAdd(deckResult, 2f);
        Entry.Logger.Info($"[DrawAndGuessMod] 你画我猜模式发放 {selectedCard.Id.Entry}（{outcome.TotalGuesses} 份猜测）。");

        // 展示最终卡牌（本地 UI，仅绘画者端，不影响同步）。
        if (openInspectScreen)
        {
            TryOpenInspectScreen(handCard);
        }
    }

    private static void TryOpenInspectScreen(CardModel card)
    {
        try
        {
            MegaCrit.Sts2.Core.Nodes.Screens.NInspectCardScreen? screen = MegaCrit.Sts2.Core.Nodes.Screens.NInspectCardScreen.Create();
            if (screen == null || Godot.Engine.GetMainLoop() is not Godot.SceneTree tree)
            {
                return;
            }

            tree.Root.AddChild(screen);
            screen.Open(new List<CardModel> { card }, 0, false);
        }
        catch (System.Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] 无法打开卡牌检视界面: {ex.Message}");
        }
    }
}
