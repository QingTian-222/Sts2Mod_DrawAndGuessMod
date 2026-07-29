using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Relics;
using DrawAndGuessMod.Scripts.State;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Random;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch(
    typeof(CardCmd),
    nameof(CardCmd.Transform),
    new[] { typeof(IEnumerable<CardTransformation>), typeof(Rng), typeof(CardPreviewStyle) })]
internal static class ErasedCardTransformPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref Task<IEnumerable<CardPileAddResult>> __result)
    {
        __result = RemoveErasedTransformResultsAsync(__result);
    }

    private static async Task<IEnumerable<CardPileAddResult>> RemoveErasedTransformResultsAsync(
        Task<IEnumerable<CardPileAddResult>> originalTask)
    {
        List<CardPileAddResult> results = (await originalTask).ToList();
        List<CardModel> erasedCards = results
            .Where(result =>
                result.success &&
                result.cardAdded?.Pile != null &&
                ErasedCardStore.IsErased(result.cardAdded.Owner.RunState, result.cardAdded.Id))
            .Select(result => result.cardAdded)
            .ToList();
        if (erasedCards.Count == 0)
        {
            return results;
        }

        var runState = erasedCards[0].Owner.RunState;
        List<CardModel> deckCards = erasedCards
            .Where(card => card.Pile?.Type == PileType.Deck)
            .ToList();
        if (deckCards.Count > 0)
        {
            await CardPileCmd.RemoveFromDeck(deckCards, showPreview: false);
        }

        List<CardModel> combatCards = erasedCards
            .Where(card => card.Pile?.IsCombatPile == true)
            .ToList();
        if (combatCards.Count > 0)
        {
            await CardPileCmd.RemoveFromCombat(combatCards);
        }

        DeathNote.TryFlashForRun(runState);
        for (int index = 0; index < results.Count; index++)
        {
            CardPileAddResult result = results[index];
            if (erasedCards.Contains(result.cardAdded))
            {
                result.success = false;
                results[index] = result;
            }
        }

        return results;
    }
}
