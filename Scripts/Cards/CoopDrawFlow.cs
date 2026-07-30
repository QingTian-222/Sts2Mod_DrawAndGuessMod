using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Config;
using DrawAndGuessMod.Scripts.State;
using DrawAndGuessMod.Scripts.Ui;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Cards;

/// <summary>协作绘画模式：所有玩家共同作画，AI 识别后由被指定的玩家三选一。</summary>
internal static class CoopDrawFlow
{
    public static async Task RunAsync(CardModel card, PlayerChoiceContext choiceContext, Player recipient, uint sessionId)
    {
        DrawingResult? drawing = await DrawingScreen.ShowAsync(
            card.Owner,
            sessionId,
            defaultTimeLimitSeconds: DrawAndGuessSettings.DrawingTimeLimitSeconds);
        if (drawing == null)
        {
            return;
        }

        ICombatState? combatState = card.Owner.Creature.CombatState;
        if (combatState == null)
        {
            Entry.Logger.Warn("[DrawAndGuessMod] Combat ended before the guessed card could be created.");
            return;
        }

        List<CardModel> options = drawing.Guess.NearestCards
            .Take(3)
            .Select(candidate => drawing.SkipAddingToDeck
                ? combatState.CreateCard(candidate, recipient)
                : card.Owner.RunState.CreateCard(candidate, recipient))
            .ToList();
        if (options.Count == 0)
        {
            Entry.Logger.Warn("[DrawAndGuessMod] The classifier returned no card choices.");
            return;
        }

        if (card.IsUpgraded)
        {
            foreach (CardModel option in options.Where(option => option.IsUpgradable))
            {
                CardCmd.Upgrade(option);
            }
        }

        CardModel? selectedCard = await CardSelectCmd.FromChooseACardScreen(choiceContext, options, recipient);
        if (selectedCard == null)
        {
            return;
        }

        ArtworkStore.Set(card.Owner.RunState, selectedCard.Id.Entry, drawing.PngBytes);

        if (drawing.SkipAddingToDeck)
        {
            CardPileAddResult handResult = await CardPileCmd.AddGeneratedCardToCombat(
                selectedCard,
                PileType.Hand,
                card.Owner);
            if (!handResult.success)
            {
                Entry.Logger.Warn($"[DrawAndGuessMod] Failed to add selected card {selectedCard.Id.Entry} to hand.");
                return;
            }

            int handOnlyRank = options.FindIndex(option => ReferenceEquals(option, selectedCard)) + 1;
            Entry.Logger.Info($"[DrawAndGuessMod] Recipient {recipient.NetId} selected hand-only card {selectedCard.Id.Entry} at AI rank {handOnlyRank}; card played by {card.Owner.NetId}.");
            return;
        }

        CardPileAddResult deckResult = await CardPileCmd.Add(selectedCard, PileType.Deck);
        if (!deckResult.success)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to add selected card {selectedCard.Id.Entry} to deck.");
            return;
        }

        CardModel addedDeckCard = deckResult.cardAdded;
        CardModel handCard = combatState.CloneCard(addedDeckCard);
        handCard.DeckVersion = addedDeckCard;
        await CardPileCmd.Add(handCard, PileType.Hand);
        CardCmd.PreviewCardPileAdd(deckResult, 2f);
        int selectedRank = options.FindIndex(option => ReferenceEquals(option, selectedCard)) + 1;
        Entry.Logger.Info($"[DrawAndGuessMod] Recipient {recipient.NetId} selected {selectedCard.Id.Entry} at AI rank {selectedRank}; card played by {card.Owner.NetId}.");
    }
}
