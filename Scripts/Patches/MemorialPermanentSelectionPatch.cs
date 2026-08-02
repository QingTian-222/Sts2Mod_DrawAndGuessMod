using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.State;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch]
internal static class MemorialPermanentSelectionPatch
{
    private const string ToggleName = "DrawAndGuessModArtworkToggle";
    private const string SimpleCardsViewScenePath =
        "res://scenes/screens/simple_cards_view_screen.tscn";
    private static readonly ConditionalWeakTable<NInspectCardScreen, InspectState> States = new();
    private static readonly ConditionalWeakTable<NInspectCardScreen, BrokenMarker> BrokenScreens = new();
    private static readonly AccessTools.FieldRef<NInspectCardScreen, List<CardModel>?> Cards =
        AccessTools.FieldRefAccess<NInspectCardScreen, List<CardModel>?>("_cards");
    private static readonly AccessTools.FieldRef<NInspectCardScreen, int> Index =
        AccessTools.FieldRefAccess<NInspectCardScreen, int>("_index");
    private static readonly MethodInfo UpdateCardDisplayMethod =
        AccessTools.Method(typeof(NInspectCardScreen), "UpdateCardDisplay");
    [ThreadStatic]
    private static int _cardLibraryOpenDepth;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NInspectCardScreen), nameof(NInspectCardScreen.Open))]
    private static void InspectOpenPostfix(NInspectCardScreen __instance, List<CardModel> cards)
    {
        if (BrokenScreens.TryGetValue(__instance, out _))
        {
            return;
        }

        try
        {
            InspectState state = EnsureState(__instance);
            state.Mode = _cardLibraryOpenDepth > 0
                ? InspectMode.CardLibrary
                : cards.Exists(card =>
                    MemorialArtworkPreviewRegistry.TryGet(card, out _, out _))
                    ? InspectMode.Memorial
                    : InspectMode.None;
            RefreshControl(__instance, state);
        }
        catch (Exception ex)
        {
            DisableBrokenControl(__instance, "open", ex);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NInspectCardScreen), "SetCard")]
    private static void SetCardPostfix(NInspectCardScreen __instance)
    {
        if (!States.TryGetValue(__instance, out InspectState? state))
        {
            return;
        }

        try
        {
            RefreshControl(__instance, state);
        }
        catch (Exception ex)
        {
            DisableBrokenControl(__instance, "change card", ex);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NInspectCardScreen), nameof(NInspectCardScreen.Close))]
    private static void InspectClosePostfix(NInspectCardScreen __instance)
    {
        if (!States.TryGetValue(__instance, out InspectState? state))
        {
            return;
        }

        try
        {
            state.Mode = InspectMode.None;
            RefreshControl(__instance, state);
        }
        catch (Exception ex)
        {
            DisableBrokenControl(__instance, "close", ex);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NCardLibrary), "ShowCardDetail")]
    private static void CardLibraryShowPrefix()
    {
        _cardLibraryOpenDepth++;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(NCardLibrary), "ShowCardDetail")]
    private static Exception? CardLibraryShowFinalizer(Exception? __exception)
    {
        _cardLibraryOpenDepth = Math.Max(0, _cardLibraryOpenDepth - 1);
        return __exception;
    }

    private static InspectState EnsureState(NInspectCardScreen screen)
    {
        if (States.TryGetValue(screen, out InspectState? existing))
        {
            return existing;
        }

        NTickbox source = screen.GetNode<NTickbox>("%Upgrade");
        NTickbox toggle = CreateNativeTickbox(source);
        screen.AddChild(toggle);
        ValidateNativeTickbox(toggle);

        MegaLabel label = toggle.GetNode<MegaLabel>("ViewUpgradesLabel");
        InspectState state = new(
            source,
            toggle,
            label,
            source.OffsetLeft,
            source.OffsetRight);
        States.Add(screen, state);
        toggle.Connect(
            NTickbox.SignalName.Toggled,
            Callable.From<NTickbox>(_ => OnArtworkToggled(screen, state)));
        toggle.IsTicked = false;
        toggle.Disable();
        return state;
    }

    private static NTickbox CreateNativeTickbox(NTickbox source)
    {
        PackedScene screenScene = ResourceLoader.Load<PackedScene>(
            SimpleCardsViewScenePath);
        Node temporaryScreen = screenScene.Instantiate();
        NTickbox toggle;
        try
        {
            toggle = temporaryScreen.GetNode<NTickbox>(
                "ViewUpgrades/MarginContainer/Upgrades");
            toggle.GetParent().RemoveChild(toggle);
            ReassignSceneOwner(toggle, toggle);
        }
        finally
        {
            temporaryScreen.Free();
        }

        toggle.Name = ToggleName;
        toggle.Visible = false;
        toggle.CustomMinimumSize = new Vector2(410f, 64f);
        toggle.FocusMode = source.FocusMode;
        toggle.MouseFilter = Control.MouseFilterEnum.Stop;
        toggle.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        toggle.AnchorLeft = 0.5f;
        toggle.AnchorTop = 0.5f;
        toggle.AnchorRight = 0.5f;
        toggle.AnchorBottom = 0.5f;
        toggle.OffsetTop = source.OffsetTop;
        toggle.OffsetBottom = source.OffsetBottom;
        toggle.AddThemeConstantOverride("separation", 8);

        foreach (Node descendant in EnumerateDescendants(toggle))
        {
            if (descendant is Control control)
            {
                control.MouseFilter = Control.MouseFilterEnum.Ignore;
            }
        }
        MegaLabel label = toggle.GetNode<MegaLabel>("ViewUpgradesLabel");
        label.MinFontSize = 28;
        label.MaxFontSize = 32;
        label.AddThemeFontSizeOverride("font_size", 32);
        return toggle;
    }

    private static void ValidateNativeTickbox(NTickbox toggle)
    {
        Control? visuals = toggle.GetNodeOrNull<Control>("%TickboxVisuals");
        if (!toggle.IsNodeReady() ||
            visuals == null ||
            visuals.Material is not ShaderMaterial ||
            toggle.GetNodeOrNull<Control>("%TickboxVisuals/Ticked") == null ||
            toggle.GetNodeOrNull<Control>("%TickboxVisuals/NotTicked") == null)
        {
            throw new InvalidOperationException(
                "The native artwork NTickbox did not initialize its visual nodes.");
        }
    }

    private static void ReassignSceneOwner(Node node, Node owner)
    {
        foreach (Node child in node.GetChildren())
        {
            child.Owner = owner;
            ReassignSceneOwner(child, owner);
        }
    }

    private static IEnumerable<Node> EnumerateDescendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node descendant in EnumerateDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RefreshControl(NInspectCardScreen screen, InspectState state)
    {
        CardModel? card = GetCurrentCard(screen);
        MemorialArtworkData? artwork = null;
        bool isMemorialPage = card != null &&
                              MemorialArtworkPreviewRegistry.TryGet(card, out artwork, out _);
        bool visible = state.Mode == InspectMode.CardLibrary ||
                       state.Mode == InspectMode.Memorial && isMemorialPage;

        if (!visible || card == null)
        {
            state.Toggle.IsTicked = false;
            state.Toggle.Disable();
            state.Toggle.Visible = false;
            RestoreUpgradePosition(state);
            return;
        }

        PositionControls(state);
        state.Toggle.Visible = true;
        state.Toggle.MouseFilter = Control.MouseFilterEnum.Stop;

        if (state.Mode == InspectMode.Memorial)
        {
            state.Label.SetTextAutoSize(ModText.Get(
                "设为永久卡面",
                "Set as Permanent Artwork"));
            state.Toggle.IsTicked = MemorialSketchbookStore.IsPermanentArtwork(artwork!);
            state.Toggle.Enable();
            return;
        }

        state.Label.SetTextAutoSize(ModText.Get("手绘风", "Hand-drawn Style"));
        bool hasArtwork = MemorialSketchbookStore.HasPermanentArtwork(card);
        state.Toggle.IsTicked = hasArtwork &&
                                 MemorialSketchbookStore.IsPermanentArtworkEnabled(card);
        state.Toggle.SetEnabled(hasArtwork);
    }

    private static void PositionControls(InspectState state)
    {
        if (state.Source.Visible)
        {
            state.Source.OffsetLeft = -430f;
            state.Source.OffsetRight = -20f;
            state.Toggle.OffsetLeft = 20f;
            state.Toggle.OffsetRight = 430f;
            return;
        }

        RestoreUpgradePosition(state);
        state.Toggle.OffsetLeft = -205f;
        state.Toggle.OffsetRight = 205f;
    }

    private static void OnArtworkToggled(NInspectCardScreen screen, InspectState state)
    {
        try
        {
            CardModel? card = GetCurrentCard(screen);
            if (card == null)
            {
                return;
            }

            if (state.Mode == InspectMode.Memorial &&
                MemorialArtworkPreviewRegistry.TryGet(
                    card,
                    out MemorialArtworkData artwork,
                    out _))
            {
                if (state.Toggle.IsTicked)
                {
                    MemorialSketchbookStore.SetPermanentArtwork(artwork);
                }
                else
                {
                    MemorialSketchbookStore.ClearPermanentArtwork(artwork);
                }
            }
            else if (state.Mode == InspectMode.CardLibrary &&
                     MemorialSketchbookStore.HasPermanentArtwork(card))
            {
                MemorialSketchbookStore.SetPermanentArtworkEnabled(
                    card,
                    state.Toggle.IsTicked);
            }

            UpdateCardDisplayMethod.Invoke(screen, null);
            RefreshControl(screen, state);
        }
        catch (Exception ex)
        {
            DisableBrokenControl(screen, "toggle artwork", ex);
        }
    }

    private static CardModel? GetCurrentCard(NInspectCardScreen screen)
    {
        List<CardModel>? cards = Cards(screen);
        int index = Index(screen);
        return cards != null && index >= 0 && index < cards.Count
            ? cards[index]
            : null;
    }

    private static void RestoreUpgradePosition(InspectState state)
    {
        state.Source.OffsetLeft = state.SourceOffsetLeft;
        state.Source.OffsetRight = state.SourceOffsetRight;
    }

    private static void DisableBrokenControl(
        NInspectCardScreen screen,
        string operation,
        Exception exception)
    {
        Entry.Logger.Error(
            $"[DrawAndGuessMod] Failed to {operation} inspect artwork control; " +
            $"the original card inspector will remain available. {exception}");
        Control? toggle = screen.GetNodeOrNull<Control>(ToggleName);
        if (toggle != null)
        {
            toggle.Visible = false;
            toggle.MouseFilter = Control.MouseFilterEnum.Ignore;
            toggle.QueueFree();
        }
        if (States.TryGetValue(screen, out InspectState? state))
        {
            RestoreUpgradePosition(state);
            States.Remove(screen);
        }
        BrokenScreens.Remove(screen);
        BrokenScreens.Add(screen, new BrokenMarker());
    }

    private enum InspectMode
    {
        None,
        Memorial,
        CardLibrary
    }

    private sealed record InspectState(
        NTickbox Source,
        NTickbox Toggle,
        MegaLabel Label,
        float SourceOffsetLeft,
        float SourceOffsetRight)
    {
        public InspectMode Mode { get; set; }
    }

    private sealed class BrokenMarker
    {
    }
}
