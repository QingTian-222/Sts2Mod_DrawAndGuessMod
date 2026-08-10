using DrawAndGuessMod.Scripts.Events;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.State;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch(typeof(NEventLayout), nameof(NEventLayout.SetEvent))]
internal static class RelicAppraisalFairPortraitLayoutPatch
{
    private static void Postfix(NEventLayout __instance, EventModel eventModel)
    {
        if (eventModel is not RelicAppraisalFair)
        {
            return;
        }

        TextureRect? portrait =
            __instance.GetNodeOrNull<TextureRect>("%Portrait");
        if (portrait == null)
        {
            return;
        }

        portrait.Scale = Vector2.One;
        portrait.StretchMode =
            TextureRect.StretchModeEnum.KeepAspectCentered;
    }
}

[HarmonyPatch(typeof(NRelic), "Reload")]
internal static class RelicAppraisalFairRelicIconPatch
{
    private static void Postfix(NRelic __instance)
    {
        if (!__instance.IsNodeReady() ||
            HasAncestor<NRelicCollectionEntry>(__instance))
        {
            return;
        }

        MegaCrit.Sts2.Core.Models.RelicModel model;
        try
        {
            model = __instance.Model;
        }
        catch (System.InvalidOperationException)
        {
            return;
        }

        bool isAppraisalStand =
            RelicAppraisalFairArtworkStore.IsPickingActive &&
            HasAncestor<NTreasureRoomRelicHolder>(__instance);
        bool isAwarded =
            RelicAppraisalFairArtworkStore.TryGetAwarded(
                model,
                out RelicAppraisalFairPresentation? awardedPresentation);
        if (!isAppraisalStand &&
            !isAwarded)
        {
            return;
        }

        RelicAppraisalFairPresentation? presentation =
            awardedPresentation;
        if (presentation == null &&
            !RelicAppraisalFairArtworkStore.TryGet(
                model,
                out presentation))
        {
            return;
        }
        __instance.Icon.Texture = presentation.Artwork;
        __instance.Outline.Visible = false;
    }

    private static bool HasAncestor<T>(Node node) where T : Node
    {
        for (Node? parent = node.GetParent();
             parent != null;
             parent = parent.GetParent())
        {
            if (parent is T)
            {
                return true;
            }
        }

        return false;
    }
}

[HarmonyPatch(
    typeof(RelicModel),
    nameof(RelicModel.Icon),
    MethodType.Getter)]
internal static class RelicAppraisalFairRelicTriggerIconPatch
{
    private static void Postfix(
        RelicModel __instance,
        ref Texture2D __result)
    {
        if (RelicAppraisalFairArtworkStore.TryGetTriggerArtwork(
                __instance,
                out Texture2D? triggerArtwork))
        {
            __result = triggerArtwork;
        }
    }
}

[HarmonyPatch(typeof(NRelicInventoryHolder), "DoFlash")]
internal static class RelicAppraisalFairInventoryFlashPatch
{
    private static readonly AccessTools.FieldRef<
        NRelicInventoryHolder,
        NRelic> Relic =
        AccessTools.FieldRefAccess<NRelicInventoryHolder, NRelic>("_relic");

    private static void Prefix(
        NRelicInventoryHolder __instance,
        ref RelicModel? __state)
    {
        __state = RelicAppraisalFairArtworkStore.PushTriggerIconContext(
            Relic(__instance).Model);
    }

    private static System.Exception? Finalizer(
        System.Exception? __exception,
        RelicModel? __state)
    {
        RelicAppraisalFairArtworkStore.RestoreTriggerIconContext(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(NRelicFlashVfx), "StartVfx")]
internal static class RelicAppraisalFairCombatFlashPatch
{
    private static readonly AccessTools.FieldRef<
        NRelicFlashVfx,
        RelicModel> Relic =
        AccessTools.FieldRefAccess<NRelicFlashVfx, RelicModel>("_relic");

    private static void Prefix(
        NRelicFlashVfx __instance,
        ref RelicModel? __state)
    {
        __state = RelicAppraisalFairArtworkStore.PushTriggerIconContext(
            Relic(__instance));
    }

    private static System.Exception? Finalizer(
        System.Exception? __exception,
        RelicModel? __state)
    {
        RelicAppraisalFairArtworkStore.RestoreTriggerIconContext(__state);
        return __exception;
    }
}

[HarmonyPatch(
    typeof(RelicModel),
    nameof(RelicModel.HoverTip),
    MethodType.Getter)]
internal static class RelicAppraisalFairRelicNamePatch
{
    private static void Postfix(
        RelicModel __instance,
        ref HoverTip __result)
    {
        if (!__instance.IsMutable ||
            !RelicAppraisalFairArtworkStore.TryGetAwarded(
                __instance,
                out RelicAppraisalFairPresentation? presentation))
        {
            return;
        }

        HoverTip renamed = new(
            CreateWorkTitle(presentation.WorkTitle),
            __result.Description,
            presentation.Artwork);
        renamed.SetCanonicalModel(__instance.CanonicalInstance);
        __result = renamed;
    }

    internal static LocString CreateWorkTitle(string workTitle)
    {
        LocString title = new(
            "events",
            "DRAW_AND_GUESS_MOD_EVENT_RELIC_APPRAISAL_FAIR.work.title");
        title.Add("Title", workTitle);
        return title;
    }
}

[HarmonyPatch(typeof(NInspectRelicScreen), "UpdateRelicDisplay")]
internal static class RelicAppraisalFairInspectRelicPatch
{
    private static readonly AccessTools.FieldRef<
        NInspectRelicScreen,
        IReadOnlyList<RelicModel>> Relics =
        AccessTools.FieldRefAccess<
            NInspectRelicScreen,
            IReadOnlyList<RelicModel>>("_relics");
    private static readonly AccessTools.FieldRef<
        NInspectRelicScreen,
        int> Index =
        AccessTools.FieldRefAccess<NInspectRelicScreen, int>("_index");
    private static readonly AccessTools.FieldRef<
        NInspectRelicScreen,
        MegaLabel> NameLabel =
        AccessTools.FieldRefAccess<NInspectRelicScreen, MegaLabel>("_nameLabel");
    private static readonly AccessTools.FieldRef<
        NInspectRelicScreen,
        TextureRect> RelicImage =
        AccessTools.FieldRefAccess<NInspectRelicScreen, TextureRect>("_relicImage");

    private static void Postfix(NInspectRelicScreen __instance)
    {
        IReadOnlyList<RelicModel>? relics = Relics(__instance);
        int index = Index(__instance);
        if (relics == null ||
            index < 0 ||
            index >= relics.Count ||
            !relics[index].IsMutable ||
            !RelicAppraisalFairArtworkStore.TryGetAwarded(
                relics[index],
                out RelicAppraisalFairPresentation? presentation))
        {
            return;
        }

        NameLabel(__instance).SetTextAutoSize(presentation.WorkTitle);
        RelicImage(__instance).Texture = presentation.Artwork;
    }
}

[HarmonyPatch(
    typeof(NTreasureRoomRelicHolder),
    nameof(NTreasureRoomRelicHolder.Initialize))]
internal static class RelicAppraisalFairRelicGlowPatch
{
    private static void Postfix(NTreasureRoomRelicHolder __instance)
    {
        if (!RelicAppraisalFairArtworkStore.IsPickingActive ||
            !RelicAppraisalFairArtworkStore.TryGet(
                __instance.Relic.Model,
                out _))
        {
            return;
        }

        HideGlow(__instance.GetNodeOrNull<GpuParticles2D>("%UncommonGlow"));
        ShowAppraisalGlow(
            __instance.GetNodeOrNull<GpuParticles2D>("%RareGlow"));
    }

    private static void HideGlow(GpuParticles2D? glow)
    {
        if (glow == null)
        {
            return;
        }

        glow.Emitting = false;
        glow.Visible = false;
    }

    private static void ShowAppraisalGlow(GpuParticles2D? glow)
    {
        if (glow == null)
        {
            return;
        }

        if (glow.ProcessMaterial is ParticleProcessMaterial source)
        {
            ParticleProcessMaterial material =
                (ParticleProcessMaterial)source.Duplicate();
            material.Color = new Color(0.48f, 0.72f, 1f, 0.48f);
            material.HueVariationMin = -0.02f;
            material.HueVariationMax = 0.04f;
            glow.ProcessMaterial = material;
        }

        glow.Modulate = Colors.White;
        glow.Visible = true;
        glow.Emitting = true;
        glow.Restart();
    }
}

[HarmonyPatch(typeof(NTreasureRoomRelicHolder), "OnFocus")]
internal static class RelicAppraisalFairRelicHoverPatch
{
    private static void Postfix(NTreasureRoomRelicHolder __instance)
    {
        if (!RelicAppraisalFairArtworkStore.IsPickingActive ||
            !RelicAppraisalFairArtworkStore.TryGet(
                __instance.Relic.Model,
                out RelicAppraisalFairPresentation? presentation))
        {
            return;
        }

        string artist = PlatformUtil.GetPlayerNameRaw(
            RunManager.Instance.NetService.Platform,
            presentation.ArtistId);
        string description = ModText.Format(
            "DRAW_AND_GUESS_MOD.RELIC_APPRAISAL_FAIR_ARTWORK_PATCH.SIGNED_BY",
            ("Artist", artist));
        HoverTip hoverTip = new(
            RelicAppraisalFairRelicNamePatch.CreateWorkTitle(
                presentation.WorkTitle),
            description,
            presentation.Artwork);
        NHoverTipSet.Remove(__instance);
        NHoverTipSet? tipSet = NHoverTipSet.CreateAndShow(
            __instance,
            hoverTip);
        if (tipSet == null)
        {
            return;
        }

        tipSet.ZAsRelative = false;
        tipSet.ZIndex = 100;
        tipSet.SetAlignmentForRelic(__instance.Relic);
    }
}
