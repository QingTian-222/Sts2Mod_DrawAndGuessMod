using System.Collections.Generic;
using System.Linq;
using DrawAndGuessMod.Scripts.Cards;
using DrawAndGuessMod.Scripts.State;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch(typeof(CardPoolModel), nameof(CardPoolModel.GetUnlockedCards))]
internal static class BlankPoolAvailabilityPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref IEnumerable<CardModel> __result)
    {
        if (!DrawingRunRules.IsGameplayEnabledForCurrentRun())
        {
            __result = __result.Where(card => card is not Blank);
        }
    }
}
