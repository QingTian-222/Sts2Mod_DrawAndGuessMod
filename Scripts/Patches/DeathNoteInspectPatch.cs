using System;
using System.Collections.Generic;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.Relics;
using DrawAndGuessMod.Scripts.State;
using DrawAndGuessMod.Scripts.Ui;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;

namespace DrawAndGuessMod.Scripts.Patches;

[HarmonyPatch(typeof(NInspectRelicScreen), "UpdateRelicDisplay")]
internal static class DeathNoteInspectPatch
{
    private const string ViewButtonName = "DrawAndGuessRelicViewButton";
    private const string ViewButtonTexturePath = "res://images/ui/reward_screen/reward_skip_button.png";
    private const string ViewButtonFontPath = "res://themes/kreon_bold_glyph_space_two.tres";
    private static bool _reportedSetupError;
    private static readonly AccessTools.FieldRef<NInspectRelicScreen, IReadOnlyList<RelicModel>> Relics =
        AccessTools.FieldRefAccess<NInspectRelicScreen, IReadOnlyList<RelicModel>>("_relics");
    private static readonly AccessTools.FieldRef<NInspectRelicScreen, int> Index =
        AccessTools.FieldRefAccess<NInspectRelicScreen, int>("_index");

    private static void Postfix(NInspectRelicScreen __instance)
    {
        try
        {
            Button? viewButton = GetOrCreateViewButton(__instance);
            if (viewButton == null)
            {
                return;
            }

            RelicModel? relic = GetCurrentInspectableRelic(__instance);
            bool hasCards = relic switch
            {
                DeathNote deathNote =>
                    ErasedCardStore.GetErasedCardIds(deathNote.Owner.RunState).Count > 0,
                MemorialSketchbook memorialSketchbook =>
                    GalleryChallengeStore.GetMemorialCardIds(memorialSketchbook.Owner).Count > 0,
                _ => false
            };
            viewButton.Text = ModText.Get("\u7ffb\u9605", "Browse");
            ApplyLocalizedButtonFont(viewButton);
            viewButton.Visible = relic != null;
            viewButton.Disabled = !hasCards;
            viewButton.MouseDefaultCursorShape = hasCards
                ? Control.CursorShape.PointingHand
                : Control.CursorShape.Arrow;
        }
        catch (Exception ex)
        {
            if (_reportedSetupError)
            {
                return;
            }

            _reportedSetupError = true;
            Entry.Logger.Error(
                $"[DrawAndGuessMod] Failed to add relic card-view button; " +
                $"the vanilla relic screen will remain available. {ex}");
        }
    }

    private static Button? GetOrCreateViewButton(NInspectRelicScreen screen)
    {
        Control? description = screen.GetNodeOrNull<Control>("%RelicDescription");
        if (description == null || description.GetParent() is not VBoxContainer container)
        {
            return null;
        }

        Button? existing = container.GetNodeOrNull<Button>(ViewButtonName);
        if (existing != null)
        {
            MoveButtonBelowDescription(container, description, existing);
            return existing;
        }

        Button button = new()
        {
            Name = ViewButtonName,
            CustomMinimumSize = new Vector2(276f, 73f),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            FocusMode = Control.FocusModeEnum.All,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        ApplyButtonStyle(button);
        button.Pressed += () => OpenViewer(screen);
        container.AddChild(button);
        MoveButtonBelowDescription(container, description, button);
        return button;
    }

    private static void MoveButtonBelowDescription(VBoxContainer container, Control description, Button button)
    {
        int targetIndex = description.GetIndex() + 1;
        if (button.GetIndex() != targetIndex)
        {
            container.MoveChild(button, targetIndex);
        }
    }

    private static void OpenViewer(NInspectRelicScreen screen)
    {
        RelicModel? relic = GetCurrentInspectableRelic(screen);
        bool hasCards = relic switch
        {
            DeathNote deathNote =>
                ErasedCardStore.GetErasedCardIds(deathNote.Owner.RunState).Count > 0,
            MemorialSketchbook memorialSketchbook =>
                GalleryChallengeStore.GetMemorialCardIds(memorialSketchbook.Owner).Count > 0,
            _ => false
        };
        if (!hasCards)
        {
            return;
        }

        screen.Close();
        Callable.From(() =>
        {
            switch (relic)
            {
                case DeathNote deathNote:
                    DeathNoteCardViewer.Show(deathNote);
                    break;
                case MemorialSketchbook memorialSketchbook:
                    MemorialSketchbookCardViewer.Show(memorialSketchbook);
                    break;
            }
        }).CallDeferred();
    }

    private static void ApplyButtonStyle(Button button)
    {
        Texture2D? texture = ResourceLoader.Load<Texture2D>(ViewButtonTexturePath);
        if (texture == null)
        {
            throw new InvalidOperationException($"Unable to load vanilla button texture: {ViewButtonTexturePath}");
        }

        ApplyLocalizedButtonFont(button);
        button.AddThemeFontSizeOverride("font_size", 30);
        button.AddThemeColorOverride("font_color", new Color("FDF4E3"));
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", Colors.White);
        button.AddThemeColorOverride("font_focus_color", Colors.White);
        button.AddThemeColorOverride("font_disabled_color", new Color("AAA59B"));
        button.AddThemeColorOverride("font_outline_color", new Color("1F4045"));
        button.AddThemeConstantOverride("outline_size", 8);
        button.AddThemeStyleboxOverride("normal", CreateButtonStyle(texture, Colors.White));
        button.AddThemeStyleboxOverride("hover", CreateButtonStyle(texture, new Color("C8F4FF")));
        button.AddThemeStyleboxOverride("pressed", CreateButtonStyle(texture, new Color("96BCC4")));
        button.AddThemeStyleboxOverride("focus", CreateButtonStyle(texture, new Color("C8F4FF")));
        button.AddThemeStyleboxOverride("disabled", CreateButtonStyle(texture, new Color("777777")));
    }

    private static void ApplyLocalizedButtonFont(Button button)
    {
        Font? font = LocManager.Instance == null
            ? null
            : FontManager.GetSubstituteFont(LocManager.Instance.Language, FontType.Bold);
        font ??= ResourceLoader.Load<Font>(ViewButtonFontPath);
        if (font != null)
        {
            button.AddThemeFontOverride("font", font);
        }
    }

    private static StyleBoxTexture CreateButtonStyle(Texture2D texture, Color tint)
    {
        return new StyleBoxTexture
        {
            Texture = texture,
            ModulateColor = tint,
            ContentMarginLeft = 24f,
            ContentMarginRight = 24f,
            ContentMarginTop = 10f,
            ContentMarginBottom = 10f
        };
    }

    private static RelicModel? GetCurrentInspectableRelic(NInspectRelicScreen screen)
    {
        IReadOnlyList<RelicModel>? relics = Relics(screen);
        int index = Index(screen);
        if (relics == null || index < 0 || index >= relics.Count)
        {
            return null;
        }

        RelicModel relic = relics[index];
        return relic is DeathNote or MemorialSketchbook ? relic : null;
    }
}
