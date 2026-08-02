using DrawAndGuessMod.Scripts.Networking;
using DrawAndGuessMod.Scripts.State;
using DrawAndGuessMod.Scripts.Ui;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch(typeof(NEventLayout), nameof(NEventLayout.SetEvent))]
internal static class NeowRunSettingsPatch
{
    private const string ButtonName = "DrawAndGuessMod_NeowSettingsButton";

    [HarmonyPostfix]
    private static void Postfix(NEventLayout __instance, EventModel eventModel)
    {
        try
        {
            if (eventModel is not Neow ||
                __instance is not NAncientEventLayout ||
                eventModel.Owner?.RunState is not RunState runState ||
                __instance.GetNodeOrNull<NeowSettingsBadge>(ButtonName) != null)
            {
                return;
            }

            if (DrawingNetSync.IsLocalHost && !DrawingRunRules.IsConfigured(runState))
            {
                DrawingRunRules.ApplyHostDefaults(runState);
            }

            Control? optionsContainer = __instance.GetNodeOrNull<Control>("%OptionsContainer");
            if (optionsContainer == null)
            {
                Entry.Logger.Warn("[DrawAndGuessMod] Neow options container was not found.");
                return;
            }

            NeowSettingsBadge badge = NeowSettingsBadge.Create(optionsContainer, runState);
            badge.Name = ButtonName;
            __instance.AddChild(badge);
        }
        catch (System.Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to install Neow drawing settings: {ex}");
        }
    }
}
