using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Relics;
using DrawAndGuessMod.Scripts.State;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch(typeof(Hook), nameof(Hook.ShouldAddToDeck))]
internal static class ErasedCardShouldAddToDeckPatch
{
    [HarmonyPostfix]
    private static void Postfix(
        IRunState runState,
        CardModel card,
        ref AbstractModel? preventer,
        ref bool __result)
    {
        if (!__result || !ErasedCardStore.IsErased(runState, card.Id))
        {
            return;
        }

        __result = false;
        preventer = DeathNote.FindForRun(runState);
        preventer ??= card;
    }
}

[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.AddGeneratedCardToCombat),
    new[] { typeof(CardModel), typeof(PileType), typeof(Player), typeof(CardPilePosition) })]
internal static class ErasedGeneratedCardPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CardModel card, ref Task<CardPileAddResult> __result)
    {
        if (!ErasedCardStore.IsErased(card.Owner.RunState, card.Id))
        {
            return true;
        }

        var runState = card.Owner.RunState;
        card.RemoveFromState();
        DeathNote.TryFlashForRun(runState);
        __result = Task.FromResult(new CardPileAddResult
        {
            success = false,
            cardAdded = card
        });
        Entry.Logger.Info(
            $"[DrawAndGuessMod] Prevented erased generated card {card.Id.Entry}.");
        return false;
    }
}

[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.AddGeneratedCardsToCombat),
    new[] { typeof(IEnumerable<CardModel>), typeof(PileType), typeof(Player), typeof(CardPilePosition) })]
internal static class ErasedGeneratedCardsPatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        ref IEnumerable<CardModel> cards,
        ref Task<IReadOnlyList<CardPileAddResult>> __result)
    {
        List<CardModel> materialized = cards.ToList();
        List<CardModel> erased = materialized
            .Where(card => ErasedCardStore.IsErased(card.Owner.RunState, card.Id))
            .ToList();
        if (erased.Count == 0)
        {
            cards = materialized;
            return true;
        }

        var runState = erased[0].Owner.RunState;
        foreach (CardModel card in erased)
        {
            card.RemoveFromState();
        }
        DeathNote.TryFlashForRun(runState);

        List<CardModel> allowed = materialized.Except(erased).ToList();
        Entry.Logger.Info(
            $"[DrawAndGuessMod] Prevented {erased.Count} erased generated card(s): " +
            string.Join(", ", erased.Select(card => card.Id.Entry).Distinct(StringComparer.Ordinal)));
        if (allowed.Count > 0)
        {
            cards = allowed;
            return true;
        }

        IReadOnlyList<CardPileAddResult> failedResults = erased
            .Select(card => new CardPileAddResult
            {
                success = false,
                cardAdded = card
            })
            .ToList();
        __result = Task.FromResult(failedResults);
        return false;
    }
}

[HarmonyPatch]
internal static class ErasedCardPilePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        Type[] sharedParameters =
        [
            typeof(IEnumerable<CardModel>),
            typeof(CardPile),
            typeof(CardPilePosition),
            typeof(AbstractModel),
            typeof(bool)
        ];

        return typeof(CardPileCmd)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(CardPileCmd.Add))
            .Where(method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length is 5 or 6 &&
                       sharedParameters
                           .Select((type, index) => parameters[index].ParameterType == type)
                           .All(matches => matches) &&
                       (parameters.Length == 5 || parameters[5].ParameterType == typeof(bool));
            });
    }

    [HarmonyPostfix]
    private static void Postfix(ref Task<IReadOnlyList<CardPileAddResult>> __result)
    {
        __result = RemoveErasedCardsAfterAddAsync(__result);
    }

    private static async Task<IReadOnlyList<CardPileAddResult>> RemoveErasedCardsAfterAddAsync(
        Task<IReadOnlyList<CardPileAddResult>> originalTask)
    {
        IReadOnlyList<CardPileAddResult> originalResults = await originalTask;
        if (originalResults.Count == 0)
        {
            return originalResults;
        }

        List<CardPileAddResult> results = originalResults.ToList();
        List<CardModel> cardsToRemove = new();
        bool changed = false;
        for (int index = 0; index < results.Count; index++)
        {
            CardPileAddResult result = results[index];
            CardModel? card = result.cardAdded;
            if (card == null ||
                !ErasedCardStore.IsErased(card.Owner.RunState, card.Id))
            {
                continue;
            }

            if (result.success && card.Pile != null)
            {
                cardsToRemove.Add(card);
            }
            else if (card.Pile == null && !card.HasBeenRemovedFromState)
            {
                card.RemoveFromState();
            }
            result.success = false;
            results[index] = result;
            changed = true;
        }

        if (cardsToRemove.Count == 0)
        {
            return changed ? results : originalResults;
        }

        var runState = cardsToRemove[0].Owner.RunState;
        List<CardModel> deckCards = cardsToRemove
            .Where(card => card.Pile?.Type == PileType.Deck)
            .ToList();
        if (deckCards.Count > 0)
        {
            await CardPileCmd.RemoveFromDeck(deckCards, showPreview: false);
        }

        List<CardModel> combatCards = cardsToRemove
            .Where(card => card.Pile?.IsCombatPile == true)
            .ToList();
        if (combatCards.Count > 0)
        {
            await CardPileCmd.RemoveFromCombat(combatCards);
        }

        Entry.Logger.Info(
            $"[DrawAndGuessMod] Prevented {cardsToRemove.Count} erased card(s) from entering a pile: " +
            string.Join(", ", cardsToRemove.Select(card => card.Id.Entry).Distinct(StringComparer.Ordinal)));
        DeathNote.TryFlashForRun(runState);
        return results;
    }
}
