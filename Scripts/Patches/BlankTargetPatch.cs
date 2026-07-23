using DrawAndGuessMod.Scripts.Cards;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch(typeof(CardModel), nameof(CardModel.IsValidTarget))]
internal static class BlankTargetPatch
{
    private static void Postfix(CardModel __instance, Creature? target, ref bool __result)
    {
        if (__instance is not Blank || target == null)
        {
            return;
        }

        __result = target.IsPlayer && target.IsAlive;
    }
}
