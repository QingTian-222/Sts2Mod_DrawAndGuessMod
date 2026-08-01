using DrawAndGuessMod.Scripts.State;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch(typeof(AbstractModel), nameof(AbstractModel.MutableClone))]
internal static class MemorialArtworkClonePatch
{
    [HarmonyPostfix]
    private static void Postfix(AbstractModel __instance, AbstractModel __result)
    {
        MemorialArtworkPreviewRegistry.Propagate(__instance, __result);
    }
}
