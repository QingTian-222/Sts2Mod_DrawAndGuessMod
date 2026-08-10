using System;
using System.Collections.Generic;
using DrawAndGuessMod.Scripts.Assets;
using DrawAndGuessMod.Scripts.Localization;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Ui;

internal sealed partial class NeowSettingsBadge : Button
{
    private const float BadgeSize = 132f;
    private Control _optionsContainer = null!;
    private RunState _runState = null!;
    private BadgeOverlay _overlay = null!;

    public static NeowSettingsBadge Create(Control optionsContainer, RunState runState)
    {
        return new NeowSettingsBadge
        {
            _optionsContainer = optionsContainer,
            _runState = runState
        };
    }

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(BadgeSize, BadgeSize);
        Size = new Vector2(BadgeSize, BadgeSize);
        PivotOffset = Size * 0.5f;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        TooltipText = ModText.Get("DRAW_AND_GUESS_MOD.NEOW_SETTINGS_BADGE.VIEW_DRAWING_SETTINGS_FOR_THIS_RUN");
        AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        AddThemeStyleboxOverride("hover", new StyleBoxEmpty());
        AddThemeStyleboxOverride("pressed", new StyleBoxEmpty());
        AddThemeStyleboxOverride("focus", new StyleBoxEmpty());

        BadgeBackground background = new();
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        background.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(background);

        if (DrawAndGuessAssets.NeowSettingsIcon is Texture2D texture)
        {
            TextureRect image = new()
            {
                Texture = texture,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                MouseFilter = MouseFilterEnum.Ignore,
                Material = CreateCircleMaskMaterial()
            };
            image.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            AddChild(image);
        }

        _overlay = new BadgeOverlay();
        _overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _overlay.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_overlay);

        Label caption = new()
        {
            Text = ModText.Get("DRAW_AND_GUESS_MOD.NEOW_SETTINGS_BADGE.SETTINGS"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        caption.SetAnchorsPreset(LayoutPreset.BottomWide);
        caption.OffsetTop = -43f;
        caption.OffsetBottom = -9f;
        caption.AddThemeFontSizeOverride("font_size", 19);
        caption.AddThemeColorOverride("font_color", Colors.White);
        caption.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.85f));
        caption.AddThemeConstantOverride("shadow_offset_x", 1);
        caption.AddThemeConstantOverride("shadow_offset_y", 2);
        caption.ApplyLocaleFontSubstitution(FontType.Regular, ThemeConstants.Label.Font);
        AddChild(caption);

        MouseEntered += () => SetHovered(true);
        MouseExited += () => SetHovered(false);
        ButtonDown += () => Scale = new Vector2(0.96f, 0.96f);
        ButtonUp += () => Scale = Vector2.One;
        Pressed += () => NeowRunSettingsScreen.Open(_runState);
        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (!GodotObject.IsInstanceValid(_optionsContainer) || _optionsContainer.Size.X <= 0f)
        {
            Visible = false;
            return;
        }

        Vector2 target = _optionsContainer.GlobalPosition + new Vector2(
            _optionsContainer.Size.X + 24f,
            (_optionsContainer.Size.Y - BadgeSize) * 0.5f);
        GlobalPosition = target;
        Visible = true;
    }

    private void SetHovered(bool hovered)
    {
        if (GodotObject.IsInstanceValid(_overlay))
        {
            _overlay.Hovered = hovered;
            _overlay.QueueRedraw();
        }
    }

    private static ShaderMaterial CreateCircleMaskMaterial()
    {
        Shader shader = new()
        {
            Code = """
                shader_type canvas_item;

                void fragment() {
                    vec2 centered = UV * 2.0 - 1.0;
                    float alpha_mask = 1.0 - smoothstep(0.955, 1.0, length(centered));
                    vec4 color = texture(TEXTURE, UV);
                    COLOR = vec4(color.rgb, color.a * alpha_mask);
                }
                """
        };
        return new ShaderMaterial { Shader = shader };
    }

    private sealed partial class BadgeBackground : Control
    {
        public override void _Draw()
        {
            DrawCircle(Size * 0.5f, MathF.Min(Size.X, Size.Y) * 0.49f, new Color("82CA47"));
        }
    }

    private sealed partial class BadgeOverlay : Control
    {
        public bool Hovered { get; set; }

        public override void _Draw()
        {
            float radius = MathF.Min(Size.X, Size.Y) * 0.48f;
            Vector2 center = Size * 0.5f;
            float chordY = center.Y + radius * 0.34f;
            float angle = MathF.Asin((chordY - center.Y) / radius);
            List<Vector2> points = new();
            const int segments = 28;
            for (int index = 0; index <= segments; index++)
            {
                float current = Mathf.Lerp(MathF.PI - angle, angle, index / (float)segments);
                points.Add(center + new Vector2(MathF.Cos(current), MathF.Sin(current)) * radius);
            }
            DrawColoredPolygon(points.ToArray(), new Color(0f, 0f, 0f, 0.68f));

            Color ring = Hovered ? new Color("B7FFF2") : new Color(1f, 1f, 1f, 0.72f);
            DrawArc(center, radius, 0f, MathF.Tau, 64, ring, Hovered ? 4f : 3f, true);
        }
    }
}
