using DrawAndGuessMod.Scripts.Ui;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch(typeof(NTreasureRoom), nameof(NTreasureRoom._Ready))]
internal static class TreasureRoomRelicDrawingPatch
{
    private static void Postfix(NTreasureRoom __instance)
    {
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null || !TreasureRoomRelicDrawingFlow.ShouldRun(runState))
        {
            return;
        }

        TreasureRoomRelicDrawingFlow.Start(__instance, runState);
    }
}
