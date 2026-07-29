using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.State;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch(typeof(NRelic), "Reload")]
internal static class RelicAuctionRelicIconPatch
{
    private static void Postfix(NRelic __instance)
    {
        if (!__instance.IsNodeReady())
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

        if (!RelicAuctionArtworkStore.TryGet(
                model,
                out RelicAuctionPresentation? presentation))
        {
            return;
        }
        __instance.Icon.Texture = presentation.Artwork;
        __instance.Outline.Visible = false;
    }
}

[HarmonyPatch(
    typeof(NTreasureRoomRelicHolder),
    nameof(NTreasureRoomRelicHolder.Initialize))]
internal static class RelicAuctionRelicGlowPatch
{
    private static void Postfix(NTreasureRoomRelicHolder __instance)
    {
        if (!RelicAuctionArtworkStore.IsPickingActive ||
            !RelicAuctionArtworkStore.TryGet(
                __instance.Relic.Model,
                out _))
        {
            return;
        }

        HideGlow(__instance.GetNodeOrNull<GpuParticles2D>("%UncommonGlow"));
        HideGlow(__instance.GetNodeOrNull<GpuParticles2D>("%RareGlow"));
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
}

[HarmonyPatch(typeof(NTreasureRoomRelicHolder), "OnFocus")]
internal static class RelicAuctionRelicHoverPatch
{
    private static void Postfix(NTreasureRoomRelicHolder __instance)
    {
        if (!RelicAuctionArtworkStore.IsPickingActive ||
            !RelicAuctionArtworkStore.TryGet(
                __instance.Relic.Model,
                out RelicAuctionPresentation? presentation))
        {
            return;
        }

        string artist = PlatformUtil.GetPlayerNameRaw(
            RunManager.Instance.NetService.Platform,
            presentation.ArtistId);
        LocString title = new(
            "events",
            "DRAW_AND_GUESS_MOD_EVENT_RELIC_AUCTION.work.title");
        title.Add("Title", presentation.WorkTitle);
        string description = ModText.Get(
            $"作者：{artist}\n真正的遗物将在拍卖结束后揭晓。",
            $"Artist: {artist}\nThe actual relic will be revealed after the auction.");
        HoverTip hoverTip = new(title, description, presentation.Artwork);
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
