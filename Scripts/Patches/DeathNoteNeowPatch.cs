using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DrawAndGuessMod.Scripts.Relics;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;

namespace DrawAndGuessMod.Scripts.Patches;

/// <summary>Adds Death Sketchbook to Neow's cursed Ancient-relic option pool.</summary>
[HarmonyPatch(typeof(Neow), "get_CurseOptions")]
internal static class DeathNoteNeowPatch
{
    private const string InitialPage = "INITIAL";
    private const string CursedDonePage = "NEOW.pages.DONE.CURSED.description";

    private static void Postfix(Neow __instance, ref IEnumerable<EventOption> __result)
    {
        List<EventOption> options = __result.ToList();
        if (options.Any(option => option.Relic is DeathNote) ||
            !CanOfferDeathNote(__instance))
        {
            __result = options;
            return;
        }

        try
        {
            MethodInfo? relicOptionMethod = AccessTools.Method(
                typeof(AncientEventModel),
                "RelicOption",
                [typeof(RelicModel), typeof(string), typeof(string)]);
            if (relicOptionMethod == null)
            {
                Entry.Logger.Warn("[DrawAndGuessMod] Could not locate Neow's relic-option factory.");
                __result = options;
                return;
            }

            RelicModel relic = ModelDb.Relic<DeathNote>().ToMutable();
            object? created = relicOptionMethod.Invoke(
                __instance,
                [relic, InitialPage, CursedDonePage]);
            if (created is EventOption option)
            {
                options.Add(option);
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to add Death Sketchbook to Neow: {ex.Message}");
        }

        __result = options;
    }

    private static bool CanOfferDeathNote(Neow neow)
    {
        if (neow.Owner == null)
        {
            return true;
        }

        int playerCount = neow.Owner.RunState.Players.Count;
        if (playerCount <= 1)
        {
            return true;
        }

        int ownerSlot = neow.Owner.RunState.GetPlayerSlotIndex(neow.Owner);
        int designatedSlot = (int)(neow.Owner.RunState.Rng.Seed % (ulong)playerCount);
        return ownerSlot == designatedSlot;
    }
}
