using DrawAndGuessMod.Scripts.Guess;
using DrawAndGuessMod.Scripts.Localization;
using Godot;

namespace DrawAndGuessMod.Scripts.Ui;

/// <summary>
/// 绘画者提交画作后的等待界面（纯 C# 构建）：
/// 展示自己的画作、实时倒计时与猜测提交进度（k/n）。
/// 仅作展示，会话逻辑由 <see cref="GuessPhaseCoordinator"/> 驱动。
/// </summary>
public partial class DrawGuessWaitingOverlay : Control
{
    private ulong _ownerId;
    private uint _sessionId;
    private int _submitted;
    private int _expected;
    private bool _bound;

    private Label _countdownLabel = null!;
    private Label _progressLabel = null!;
    private VBoxContainer _column = null!;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 4050;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        BuildUi();
    }

    /// <summary>绑定会话与进度回调。必须在 AddChild 之后调用。</summary>
    public void Bind(ulong ownerId, uint sessionId, int expectedGuessers, byte[] pngBytes)
    {
        _ownerId = ownerId;
        _sessionId = sessionId;
        _expected = expectedGuessers;
        _submitted = 0;
        _bound = true;
        SetProgress(0, expectedGuessers);
        SetImage(pngBytes);
    }

    /// <summary>供协调器回调：更新提交进度。</summary>
    public void SetProgress(int submitted, int expected)
    {
        _submitted = submitted;
        _expected = expected;
        if (GodotObject.IsInstanceValid(_progressLabel))
        {
            _progressLabel.Text = ModText.Get(
                $"已收到猜测：{_submitted} / {_expected}",
                $"Guesses received: {_submitted} / {_expected}");
        }
    }

    public void SetImage(byte[] pngBytes)
    {
        Image image = new();
        if (image.LoadPngFromBuffer(pngBytes) != Error.Ok)
        {
            return;
        }

        TextureRect rect = new()
        {
            Texture = ImageTexture.CreateFromImage(image),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(420f, 240f),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter
        };
        _column.AddChild(rect);
        _column.MoveChild(rect, 2);
    }

    public override void _Process(double delta)
    {
        if (!_bound || !GodotObject.IsInstanceValid(_countdownLabel))
        {
            return;
        }

        double timeLeft = GuessPhaseCoordinator.GetOwnerTimeLeft(_ownerId, _sessionId);
        _countdownLabel.Text = ModText.Get(
            $"剩余时间：{Mathf.CeilToInt((float)timeLeft)} 秒",
            $"Time Left: {Mathf.CeilToInt((float)timeLeft)} s");
    }

    private void BuildUi()
    {
        ColorRect shade = new()
        {
            Color = new Color(0.01f, 0.015f, 0.025f, 0.82f),
            MouseFilter = MouseFilterEnum.Stop
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(shade);

        CenterContainer center = new();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        PanelContainer panel = new()
        {
            CustomMinimumSize = new Vector2(480f, 0f)
        };
        center.AddChild(panel);

        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        panel.AddChild(margin);

        VBoxContainer column = new();
        column.AddThemeConstantOverride("separation", 10);
        margin.AddChild(column);
        _column = column;

        Label title = new()
        {
            Text = ModText.Get("等待其他玩家猜测", "Waiting for Others to Guess"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 26);
        column.AddChild(title);

        _countdownLabel = new Label
        {
            Text = ModText.Get("剩余时间：-- 秒", "Time Left: -- s"),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _countdownLabel.AddThemeFontSizeOverride("font_size", 15);
        column.AddChild(_countdownLabel);

        _progressLabel = new Label
        {
            Text = ModText.Get("已收到猜测：0 / 0", "Guesses received: 0 / 0"),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        column.AddChild(_progressLabel);

        Label hint = new()
        {
            Text = ModText.Get("全员提交或倒计时结束后自动结算。", "Will finalize automatically when everyone guesses or time runs out."),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore
        };
        hint.AddThemeFontSizeOverride("font_size", 13);
        column.AddChild(hint);
    }
}
