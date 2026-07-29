using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.Networking;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Ui;

internal partial class RelicWorkTitlePrompt : Control
{
    private readonly TaskCompletionSource<string> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _fallbackTitle = "";

    public static Task<string> ShowAsync(string fallbackTitle)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return Task.FromResult(fallbackTitle);
        }

        RelicWorkTitlePrompt prompt = new()
        {
            _fallbackTitle = fallbackTitle
        };
        tree.Root.AddChild(prompt);
        return prompt._completion.Task;
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        ZIndex = 4100;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        ColorRect shade = new()
        {
            Color = new Color(0f, 0f, 0f, 0.82f),
            MouseFilter = MouseFilterEnum.Stop
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(shade);

        CenterContainer center = new();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        PanelContainer panel = new()
        {
            CustomMinimumSize = new Vector2(620f, 250f)
        };
        center.AddChild(panel);

        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 34);
        margin.AddThemeConstantOverride("margin_right", 34);
        margin.AddThemeConstantOverride("margin_top", 28);
        margin.AddThemeConstantOverride("margin_bottom", 28);
        panel.AddChild(margin);

        VBoxContainer content = new();
        content.AddThemeConstantOverride("separation", 18);
        margin.AddChild(content);

        Label title = new()
        {
            Text = ModText.Get("为你的作品命名", "Name Your Work"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 28);
        content.AddChild(title);

        Label hint = new()
        {
            Text = ModText.Get(
                "拍卖时只会展示作品名、作者与画作。你可以用名字伪装它。",
                "Only the title, artist, and artwork will be shown at auction. You may use the title to disguise it."),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        content.AddChild(hint);

        LineEdit input = new()
        {
            Text = _fallbackTitle,
            MaxLength = 32,
            SelectAllOnFocus = true,
            CustomMinimumSize = new Vector2(0f, 44f)
        };
        content.AddChild(input);

        Button confirm = new()
        {
            Text = ModText.Get("提交作品", "Submit Work"),
            CustomMinimumSize = new Vector2(190f, 48f),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter
        };
        content.AddChild(confirm);
        input.TextChanged += text => confirm.Disabled = string.IsNullOrWhiteSpace(text);
        confirm.Pressed += () =>
        {
            string result = string.IsNullOrWhiteSpace(input.Text)
                ? _fallbackTitle
                : input.Text.Trim();
            _completion.TrySetResult(result);
            QueueFree();
        };
        input.GrabFocus();
    }

    public override void _ExitTree()
    {
        _completion.TrySetResult(_fallbackTitle);
    }
}

internal partial class RelicAuctionSelectionScreen : Control
{
    private readonly TaskCompletionSource<ulong> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<RelicAuctionSubmission> _submissions = [];

    public static Task<ulong> ShowAsync(IReadOnlyList<RelicAuctionSubmission> submissions)
    {
        if (Engine.GetMainLoop() is not SceneTree tree || submissions.Count == 0)
        {
            return Task.FromResult(0UL);
        }

        RelicAuctionSelectionScreen screen = new()
        {
            _submissions = submissions
        };
        tree.Root.AddChild(screen);
        return screen._completion.Task;
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        ZIndex = 4050;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        ColorRect shade = new()
        {
            Color = new Color(0.012f, 0.016f, 0.024f, 0.95f),
            MouseFilter = MouseFilterEnum.Stop
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(shade);

        MarginContainer margin = new();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 26);
        AddChild(margin);

        VBoxContainer content = new();
        content.AddThemeConstantOverride("separation", 16);
        margin.AddChild(content);

        Label title = new()
        {
            Text = ModText.Get("遗物拍卖会", "Relic Auction"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 32);
        content.AddChild(title);

        Label hint = new()
        {
            Text = ModText.Get(
                "选择一幅作品参与争夺。这里只展示作品名、作者和画作，不会透露真正的遗物。",
                "Choose a work to contest. Only its title, artist, and artwork are shown; the actual relic remains hidden."),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.AddChild(hint);

        ScrollContainer scroll = new()
        {
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        content.AddChild(scroll);

        HBoxContainer works = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        works.AddThemeConstantOverride("separation", 22);
        scroll.AddChild(works);

        foreach (RelicAuctionSubmission submission in _submissions.OrderBy(item => item.OwnerId))
        {
            works.AddChild(CreateWorkPanel(submission));
        }
    }

    private Control CreateWorkPanel(RelicAuctionSubmission submission)
    {
        PanelContainer panel = new()
        {
            CustomMinimumSize = new Vector2(310f, 470f)
        };
        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        panel.AddChild(margin);

        VBoxContainer content = new();
        content.AddThemeConstantOverride("separation", 10);
        margin.AddChild(content);

        TextureRect artwork = new()
        {
            CustomMinimumSize = new Vector2(280f, 280f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
        };
        Image image = new();
        if (image.LoadPngFromBuffer(submission.PngBytes) == Error.Ok)
        {
            artwork.Texture = ImageTexture.CreateFromImage(image);
        }
        content.AddChild(artwork);

        Label workTitle = new()
        {
            Text = submission.WorkTitle,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        workTitle.AddThemeFontSizeOverride("font_size", 24);
        content.AddChild(workTitle);

        string author = PlatformUtil.GetPlayerNameRaw(
            RunManager.Instance.NetService.Platform,
            submission.OwnerId);
        Label authorLabel = new()
        {
            Text = ModText.Get($"作者：{author}", $"Artist: {author}"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.AddChild(authorLabel);

        Button choose = new()
        {
            Text = ModText.Get("选择这幅作品", "Choose This Work"),
            CustomMinimumSize = new Vector2(0f, 48f)
        };
        choose.Pressed += () =>
        {
            _completion.TrySetResult(submission.OwnerId);
            QueueFree();
        };
        content.AddChild(choose);
        return panel;
    }

    public override void _ExitTree()
    {
        _completion.TrySetResult(0UL);
    }
}
