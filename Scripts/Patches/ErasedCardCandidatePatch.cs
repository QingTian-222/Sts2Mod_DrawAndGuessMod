using System.Collections.Generic;
using System.Linq;
using DrawAndGuessMod.Scripts.State;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch(
    typeof(CardCreationOptions),
    nameof(CardCreationOptions.GetPossibleCards),
    new[] { typeof(Player) })]
internal static class ErasedRewardCandidatePatch
{
    [HarmonyPostfix]
    private static void Postfix(Player player, ref IEnumerable<CardModel> __result)
    {
        __result = ErasedCardCandidateFilter.Apply(player, __result);
    }
}

[HarmonyPatch(
    typeof(CardFactory),
    nameof(CardFactory.GetDistinctForCombat),
    new[] { typeof(Player), typeof(IEnumerable<CardModel>), typeof(int), typeof(Rng) })]
internal static class ErasedDistinctCombatCandidatePatch
{
    [HarmonyPrefix]
    private static void Prefix(Player player, ref IEnumerable<CardModel> cards)
    {
        cards = ErasedCardCandidateFilter.Apply(player, cards);
    }
}

[HarmonyPatch(
    typeof(CardFactory),
    nameof(CardFactory.GetForCombat),
    new[] { typeof(Player), typeof(IEnumerable<CardModel>), typeof(int), typeof(Rng) })]
internal static class ErasedCombatCandidatePatch
{
    [HarmonyPrefix]
    private static void Prefix(Player player, ref IEnumerable<CardModel> cards)
    {
        cards = ErasedCardCandidateFilter.Apply(player, cards);
    }
}

[HarmonyPatch(
    typeof(CardFactory),
    nameof(CardFactory.GetDefaultTransformationOptions),
    new[] { typeof(CardModel), typeof(bool) })]
internal static class ErasedDefaultTransformationCandidatePatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel original, ref IEnumerable<CardModel> __result)
    {
        __result = ErasedCardCandidateFilter.Apply(original.Owner, __result);
    }
}

[HarmonyPatch(
    typeof(CardFactory),
    nameof(CardFactory.CreateRandomCardForTransform),
    new[] { typeof(CardModel), typeof(IEnumerable<CardModel>), typeof(bool), typeof(Rng) })]
internal static class ErasedCustomTransformationCandidatePatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel original, ref IEnumerable<CardModel> options)
    {
        options = ErasedCardCandidateFilter.Apply(original.Owner, options);
    }
}

[HarmonyPatch(
    typeof(CardSelectCmd),
    nameof(CardSelectCmd.FromChooseACardScreen),
    new[]
    {
        typeof(PlayerChoiceContext),
        typeof(IReadOnlyList<CardModel>),
        typeof(Player),
        typeof(bool)
    })]
internal static class ErasedChooseACardCandidatePatch
{
    [HarmonyPrefix]
    private static void Prefix(Player player, ref IReadOnlyList<CardModel> cards)
    {
        List<CardModel> materialized = cards.ToList();
        List<CardModel> erased = materialized
            .Where(card => ErasedCardStore.IsErased(player.RunState, card.Id))
            .ToList();
        foreach (CardModel card in erased)
        {
            if (card.Pile == null && !card.HasBeenRemovedFromState)
            {
                card.RemoveFromState();
            }
        }
        cards = materialized.Except(erased).ToList();
    }
}

internal static class ErasedCardCandidateFilter
{
    public static IEnumerable<CardModel> Apply(Player player, IEnumerable<CardModel> cards)
    {
        return cards.Where(card =>
            !ErasedCardStore.IsErased(player.RunState, card.Id));
    }
}
