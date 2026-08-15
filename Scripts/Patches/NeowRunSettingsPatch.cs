using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DrawAndGuessMod.Scripts.Networking;
using DrawAndGuessMod.Scripts.Relics;
using DrawAndGuessMod.Scripts.State;
using DrawAndGuessMod.Scripts.Ui;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch(typeof(NEventLayout), nameof(NEventLayout.SetEvent))]
internal static class NeowRunSettingsPatch
{
    private const string ButtonName = "DrawAndGuessMod_NeowSettingsButton";
    private static readonly MethodInfo? SetEventStateMethod = AccessTools.Method(
        typeof(EventModel),
        "SetEventState");
    private static readonly PropertyInfo? CurseOptionsProperty = AccessTools.Property(
        typeof(Neow),
        "CurseOptions");
    private static readonly FieldInfo? GeneratedOptionsField = AccessTools.Field(
        typeof(AncientEventModel),
        "_generatedOptions");
    private static bool _gameplayReplacementHooked;

    internal static void RegisterGameplayOptionReplacement()
    {
        EnsureGameplayReplacementHook();
    }

    [HarmonyPostfix]
    private static void Postfix(NEventLayout __instance, EventModel eventModel)
    {
        try
        {
            if (eventModel is not Neow ||
                __instance is not NAncientEventLayout ||
                eventModel.Owner?.RunState is not RunState runState)
            {
                return;
            }

            EnsureGameplayReplacementHook();
            if (__instance.GetNodeOrNull<NeowSettingsBadge>(ButtonName) != null)
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

    private static void EnsureGameplayReplacementHook()
    {
        if (_gameplayReplacementHooked)
        {
            return;
        }

        DrawingRunRules.GameplayEnabledChanged += OnGameplayEnabledChanged;
        _gameplayReplacementHooked = true;
    }

    private static void OnGameplayEnabledChanged(RunState runState, bool enabled)
    {
        if (!enabled)
        {
            Callable.From(() => ReplaceVisibleDeathNoteOptions(runState)).CallDeferred();
        }
    }

    private static void ReplaceVisibleDeathNoteOptions(RunState runState)
    {
        if (CurseOptionsProperty == null || SetEventStateMethod == null)
        {
            Entry.Logger.Warn("[DrawAndGuessMod] Could not locate Neow option replacement methods.");
            return;
        }

        try
        {
            List<Neow> activeNeows = RunManager.Instance.EventSynchronizer.Events
                .OfType<Neow>()
                .Where(neow =>
                    !neow.IsFinished &&
                    ReferenceEquals(neow.Owner?.RunState, runState))
                .ToList();
            foreach (Neow neow in activeNeows)
            {
                List<EventOption> options = neow.CurrentOptions.ToList();
                int deathNoteIndex = options.FindIndex(option => option.Relic is DeathNote);
                if (deathNoteIndex < 0)
                {
                    continue;
                }

                List<EventOption> replacements = GetReplacementOptions(neow, options);
                if (replacements.Count == 0)
                {
                    Entry.Logger.Warn("[DrawAndGuessMod] No valid replacement for Death Sketchbook was found.");
                    continue;
                }

                int ownerSlot = runState.GetPlayerSlotIndex(neow.Owner!);
                ulong mixedSeed = DrawAndGuessMod.Scripts.Compatibility.RunRngCompat.GetSeed(runState.Rng) ^
                                  ((ulong)(ownerSlot + 1) * 0x9E3779B97F4A7C15UL);
                EventOption replacement = replacements[(int)(mixedSeed % (ulong)replacements.Count)];
                EventOption deathNoteOption = options[deathNoteIndex];
                options[deathNoteIndex] = replacement;
                ReplaceGeneratedOption(neow, deathNoteOption, replacement);
                SetEventStateMethod.Invoke(
                    neow,
                    [neow.Description ?? neow.InitialDescription, options]);
                Entry.Logger.Info(
                    $"[DrawAndGuessMod] Replaced Death Sketchbook in Neow's current choices with " +
                    $"{replacement.Relic?.Id.Entry ?? replacement.TextKey}.");
            }
        }
        catch (System.Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to replace Death Sketchbook at Neow: {ex}");
        }
    }

    private static List<EventOption> GetReplacementOptions(Neow neow, IReadOnlyList<EventOption> currentOptions)
    {
        if (CurseOptionsProperty?.GetValue(neow) is not IEnumerable<EventOption> curseOptions)
        {
            return new List<EventOption>();
        }

        HashSet<ModelId> visibleRelics = currentOptions
            .Where(option => option.Relic is not null)
            .Select(option => option.Relic!.Id)
            .ToHashSet();
        return curseOptions
            .Where(option =>
                option.Relic is not null &&
                option.Relic is not DeathNote &&
                !visibleRelics.Contains(option.Relic.Id) &&
                !ConflictsWithVisibleChoice(option.Relic, currentOptions) &&
                option.Relic.IsAllowedAtNeow(neow.Owner!))
            .ToList();
    }

    private static bool ConflictsWithVisibleChoice(RelicModel replacement, IReadOnlyList<EventOption> currentOptions)
    {
        if (string.Equals(replacement.GetType().Name, "NeowsSacrifice", StringComparison.Ordinal))
        {
            return HasVisibleRelic<PhialHolster>(currentOptions) ||
                   HasVisibleRelic<LostCoffer>(currentOptions);
        }

        return replacement switch
        {
            CursedPearl => HasVisibleRelic<GoldenPearl>(currentOptions),
            HeftyTablet => HasVisibleRelic<ArcaneScroll>(currentOptions),
            LeafyPoultice => HasVisibleRelic<NewLeaf>(currentOptions),
            PrecariousShears => HasVisibleRelic<PreciseScissors>(currentOptions),
            LargeCapsule =>
                HasVisibleRelic<LavaRock>(currentOptions) ||
                HasVisibleRelic<SmallCapsule>(currentOptions),
            _ => false
        };
    }

    private static bool HasVisibleRelic<TRelic>(IReadOnlyList<EventOption> currentOptions)
        where TRelic : RelicModel
    {
        return currentOptions.Any(option => option.Relic is TRelic);
    }

    private static void ReplaceGeneratedOption(Neow neow, EventOption oldOption, EventOption replacement)
    {
        if (GeneratedOptionsField?.GetValue(neow) is not List<EventOption> generatedOptions)
        {
            return;
        }

        int index = generatedOptions.FindIndex(option => ReferenceEquals(option, oldOption));
        if (index < 0)
        {
            index = generatedOptions.FindIndex(option => option.Relic is DeathNote);
        }
        if (index >= 0)
        {
            generatedOptions[index] = replacement;
        }
    }
}
