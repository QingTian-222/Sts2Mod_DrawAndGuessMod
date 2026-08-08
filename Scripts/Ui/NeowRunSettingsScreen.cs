using System;
using DrawAndGuessMod.Scripts.Config;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.Networking;
using DrawAndGuessMod.Scripts.State;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Ui;

internal sealed partial class NeowRunSettingsScreen : Control, IScreenContext
{
    private RunState _runState = null!;
    private Action? _onApplied;
    private bool _isHost;
    private CheckButton _gameplayEnabledToggle = null!;
    private OptionButton _initialBlankOption = null!;
    private OptionButton _drawingTimeOption = null!;
    private CheckButton _noRepeatCheck = null!;
    private CheckButton _skipDeckCheck = null!;
    private OptionButton _cardRestrictionOption = null!;
    private CheckButton _treasureRoomRelicDrawingCheck = null!;
    private Label _statusLabel = null!;
    private Button _applyButton = null!;
    private Button _closeButton = null!;
    private Button _precacheButton = null!;
    private ProgressBar _precacheProgressBar = null!;
    private Label _precacheProgressLabel = null!;
    private TextureRect _precacheThumbnail = null!;

    public Control? DefaultFocusedControl => _isHost ? _applyButton : _precacheButton;

    public static void Open(RunState runState, Action? onApplied = null)
    {
        NModalContainer? modalContainer = NModalContainer.Instance;
        if (modalContainer == null || modalContainer.OpenModal != null)
        {
            return;
        }

        NeowRunSettingsScreen screen = new()
        {
            Name = "DrawAndGuessMod_NeowRunSettings",
            _runState = runState,
            _onApplied = onApplied,
            _isHost = DrawingNetSync.IsLocalHost,
            ProcessMode = ProcessModeEnum.Always
        };
        modalContainer.Add(screen);
    }

    public override void _EnterTree()
    {
        DrawingRunRules.RulesChanged += OnRulesChanged;
        DrawAndGuessSettings.PretrainingChanged += OnPretrainingChanged;
    }

    public override void _ExitTree()
    {
        DrawingRunRules.RulesChanged -= OnRulesChanged;
        DrawAndGuessSettings.PretrainingChanged -= OnPretrainingChanged;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        BuildUi();
        LoadCurrentValues();
        RefreshPretrainingUi();
        DefaultFocusedControl?.GrabFocus();
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private void BuildUi()
    {
        PanelContainer panel = new()
        {
            Name = "SettingsPanel",
            CustomMinimumSize = new Vector2(720f, 760f),
            MouseFilter = MouseFilterEnum.Stop
        };
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.OffsetLeft = -360f;
        panel.OffsetTop = -380f;
        panel.OffsetRight = 360f;
        panel.OffsetBottom = 380f;
        panel.AddThemeStyleboxOverride("panel", CreatePanelStyle());
        AddChild(panel);

        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 34);
        margin.AddThemeConstantOverride("margin_right", 34);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        panel.AddChild(margin);

        VBoxContainer content = new();
        content.AddThemeConstantOverride("separation", 5);
        margin.AddChild(content);

        Label title = CreateLabel(Text("作画设置", "Drawing Settings"), 34, FontType.Bold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        content.AddChild(title);

        string subtitleText = _isHost
            ? Text("房主设置将应用并同步给所有玩家。", "Changes are applied and synchronized to all players.")
            : Text("当前设置由房主控制，本地仅可查看。", "The host controls these settings. This view is read-only.");
        Label subtitle = CreateLabel(subtitleText, 20);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        subtitle.AddThemeColorOverride("font_color", new Color("B9C2C8"));
        content.AddChild(subtitle);
        content.AddChild(new HSeparator());

        _gameplayEnabledToggle = new CheckButton
        {
            FocusMode = FocusModeEnum.All,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = Text(
                "关闭后，本局不会生成“空白”、相关事件以及绘本遗物；如果涅奥当前给出了死亡绘本，只会替换这一项。",
                "Disable this to remove Blank, related events, and sketchbook relics from this run. If Death Sketchbook is currently offered by Neow, only that choice will be replaced.")
        };
        _gameplayEnabledToggle.AddThemeFontSizeOverride("font_size", 21);
        ApplyGameFont(_gameplayEnabledToggle);
        _gameplayEnabledToggle.Toggled += enabled =>
        {
            RefreshGameplayToggleText();
            SetRuleControlsEditable(_isHost && enabled);
        };
        content.AddChild(CreateSettingRow(
            Text("开启“你画瓦猜”玩法", "Enable Draw & Guess Gameplay"),
            _gameplayEnabledToggle));

        _initialBlankOption = CreateOptionButton();
        AddOption(_initialBlankOption, Text("不获得", "None"), (int)InitialBlankMode.None);
        AddOption(_initialBlankOption, Text("随机一名玩家", "One Random Player"), (int)InitialBlankMode.RandomPlayer);
        AddOption(_initialBlankOption, Text("全体玩家", "All Players"), (int)InitialBlankMode.AllPlayers);
        content.AddChild(CreateSettingRow(Text("初始空白", "Starting Blank"), _initialBlankOption));

        _drawingTimeOption = CreateOptionButton();
        AddOption(_drawingTimeOption, Text("无限制", "Unlimited"), 0);
        AddOption(_drawingTimeOption, Text("30 秒", "30 Seconds"), 30);
        AddOption(_drawingTimeOption, Text("60 秒", "60 Seconds"), 60);
        AddOption(_drawingTimeOption, Text("90 秒", "90 Seconds"), 90);
        AddOption(_drawingTimeOption, Text("120 秒", "120 Seconds"), 120);
        content.AddChild(CreateSettingRow(Text("作画时间", "Drawing Time"), _drawingTimeOption));

        _noRepeatCheck = CreateToggle();
        _noRepeatCheck.Toggled += _ => RefreshToggleText(_noRepeatCheck);
        content.AddChild(CreateSettingRow(Text("绘画不重复", "No Repeat Cards"), _noRepeatCheck));

        _skipDeckCheck = CreateToggle();
        _skipDeckCheck.Toggled += _ => RefreshToggleText(_skipDeckCheck);
        content.AddChild(CreateSettingRow(Text("绘画不加入卡组", "Do Not Add Drawn Cards to Deck"), _skipDeckCheck));

        _cardRestrictionOption = CreateOptionButton();
        AddOption(_cardRestrictionOption, Text("无限制", "Unlimited"), (int)DrawingCardRestriction.None);
        AddOption(_cardRestrictionOption, Text("不能画出先古牌", "No Ancient Cards"), (int)DrawingCardRestriction.ExcludeAncient);
        AddOption(_cardRestrictionOption, Text("只能画出自己职业的卡牌", "Own Character Cards Only"), (int)DrawingCardRestriction.CurrentCharacter);
        content.AddChild(CreateSettingRow(Text("画图限制", "Card Restriction"), _cardRestrictionOption));

        _treasureRoomRelicDrawingCheck = CreateToggle();
        _treasureRoomRelicDrawingCheck.TooltipText = Text(
            "多人模式下，进入宝箱房后每名玩家会绘制一个由原版宝箱逻辑生成的遗物；全部提交后自动开箱。开启时不会出现“鉴宝大会”事件。",
            "In multiplayer, each player draws a relic generated by the vanilla chest logic. The chest opens after every drawing is submitted, and Relic Appraisal Fair will not appear while this is enabled.");
        _treasureRoomRelicDrawingCheck.Toggled += _ => RefreshToggleText(_treasureRoomRelicDrawingCheck);
        HBoxContainer treasureRoomRelicDrawingRow = CreateSettingRow(
            Text("宝箱房画遗物", "Draw Relics in Treasure Rooms"),
            _treasureRoomRelicDrawingCheck);
        treasureRoomRelicDrawingRow.Visible = _runState.Players.Count > 1;
        content.AddChild(treasureRoomRelicDrawingRow);
        _gameplayEnabledToggle.Disabled = !_isHost;
        SetRuleControlsEditable(_isHost && _gameplayEnabledToggle.ButtonPressed);

        content.AddChild(new HSeparator());
        HBoxContainer precacheHeader = new();
        Label precacheTitle = CreateLabel(Text("卡牌预缓存", "Card Pre-cache"), 22, FontType.Bold);
        precacheTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        precacheHeader.AddChild(precacheTitle);
        _precacheButton = CreateButton(Text("开始预缓存", "Start Pre-cache"), false);
        _precacheButton.CustomMinimumSize = new Vector2(160f, 42f);
        _precacheButton.Pressed += StartPrecache;
        precacheHeader.AddChild(_precacheButton);
        content.AddChild(precacheHeader);

        HBoxContainer precacheProgress = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        precacheProgress.AddThemeConstantOverride("separation", 12);
        _precacheThumbnail = new TextureRect
        {
            CustomMinimumSize = new Vector2(144f, 100f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        precacheProgress.AddChild(_precacheThumbnail);
        VBoxContainer precacheProgressText = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _precacheProgressBar = new ProgressBar
        {
            MinValue = 0d,
            MaxValue = 100d,
            ShowPercentage = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, 24f)
        };
        _precacheProgressBar.AddThemeFontSizeOverride("font_size", 18);
        ApplyGameFont(_precacheProgressBar);
        precacheProgressText.AddChild(_precacheProgressBar);
        _precacheProgressLabel = CreateLabel(string.Empty, 17);
        _precacheProgressLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _precacheProgressLabel.CustomMinimumSize = new Vector2(0f, 38f);
        _precacheProgressLabel.AddThemeColorOverride("font_color", new Color("B9C2C8"));
        precacheProgressText.AddChild(_precacheProgressLabel);
        precacheProgress.AddChild(precacheProgressText);
        content.AddChild(precacheProgress);

        _statusLabel = CreateLabel(string.Empty, 17);
        _statusLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _statusLabel.AddThemeColorOverride("font_color", new Color("F3B5A5"));
        _statusLabel.CustomMinimumSize = new Vector2(0f, 22f);
        content.AddChild(_statusLabel);

        HBoxContainer actions = new();
        actions.Alignment = BoxContainer.AlignmentMode.End;
        actions.AddThemeConstantOverride("separation", 12);
        _closeButton = CreateButton(_isHost ? Text("取消", "Cancel") : Text("关闭", "Close"), false);
        _closeButton.Pressed += Close;
        actions.AddChild(_closeButton);
        _applyButton = CreateButton(Text("应用", "Apply"), true);
        _applyButton.Pressed += Apply;
        _applyButton.Visible = _isHost;
        actions.AddChild(_applyButton);
        content.AddChild(actions);
    }

    private void SetRuleControlsEditable(bool editable)
    {
        _initialBlankOption.Disabled = !editable;
        _drawingTimeOption.Disabled = !editable;
        _noRepeatCheck.Disabled = !editable;
        _skipDeckCheck.Disabled = !editable;
        _cardRestrictionOption.Disabled = !editable;
        _treasureRoomRelicDrawingCheck.Disabled = !editable;
    }

    private void LoadCurrentValues()
    {
        DrawingRunRuleState state = DrawingRunRules.GetSnapshot(_runState);
        _gameplayEnabledToggle.ButtonPressed = state.GameplayEnabled;
        RefreshGameplayToggleText();
        SetRuleControlsEditable(_isHost && state.GameplayEnabled);
        SelectById(_initialBlankOption, state.InitialBlankMode);
        SelectById(_drawingTimeOption, state.DrawingTimeLimitSeconds);
        _noRepeatCheck.ButtonPressed = state.ExcludePreviouslySelectedCards;
        RefreshToggleText(_noRepeatCheck);
        _skipDeckCheck.ButtonPressed = state.BlankGeneratedCardSkipsDeck;
        RefreshToggleText(_skipDeckCheck);
        SelectById(_cardRestrictionOption, state.CardRestriction);
        _treasureRoomRelicDrawingCheck.ButtonPressed = state.TreasureRoomRelicDrawingEnabled;
        RefreshToggleText(_treasureRoomRelicDrawingCheck);
    }

    private void Apply()
    {
        if (!_isHost)
        {
            return;
        }

        try
        {
            DrawingRunRules.ApplyHostSettings(
                _runState,
                _gameplayEnabledToggle.ButtonPressed,
                (InitialBlankMode)_initialBlankOption.GetSelectedId(),
                _drawingTimeOption.GetSelectedId(),
                _noRepeatCheck.ButtonPressed,
                _skipDeckCheck.ButtonPressed,
                (DrawingCardRestriction)_cardRestrictionOption.GetSelectedId(),
                _treasureRoomRelicDrawingCheck.ButtonPressed);
            _onApplied?.Invoke();
            Close();
        }
        catch (Exception ex)
        {
            Entry.Logger.Error($"[DrawAndGuessMod] Failed to apply Neow drawing settings: {ex}");
            _statusLabel.Text = Text("应用失败：", "Failed to apply: ") + ex.Message;
        }
    }

    private void RefreshGameplayToggleText()
    {
        RefreshToggleText(_gameplayEnabledToggle);
    }

    private static void RefreshToggleText(CheckButton toggle)
    {
        toggle.Text = toggle.ButtonPressed
            ? Text("开启", "Enabled")
            : Text("关闭", "Disabled");
    }

    private void StartPrecache()
    {
        DrawAndGuessSettings.StartPretrainingFromRunSettings();
        RefreshPretrainingUi();
    }

    private void RefreshPretrainingUi()
    {
        if (_precacheButton == null || !GodotObject.IsInstanceValid(_precacheButton))
        {
            return;
        }

        bool running = DrawAndGuessSettings.IsPretraining;
        _precacheButton.Disabled = running;
        _precacheButton.Text = running
            ? Text("正在预缓存…", "Pre-caching...")
            : Text("开始预缓存", "Start Pre-cache");
        _precacheProgressBar.Value = DrawAndGuessSettings.PretrainingProgress;
        _precacheThumbnail.Texture = DrawAndGuessSettings.CurrentPretrainingThumbnail;
        _precacheThumbnail.Visible = running && _precacheThumbnail.Texture != null;
        _precacheProgressLabel.Text = string.IsNullOrWhiteSpace(DrawAndGuessSettings.CurrentPretrainingStatus)
            ? Text("尚未开始预缓存。", "Pre-cache has not started yet.")
            : DrawAndGuessSettings.CurrentPretrainingStatus;
    }

    private void OnRulesChanged(RunState runState)
    {
        if (!ReferenceEquals(runState, _runState))
        {
            return;
        }

        Callable.From(() =>
        {
            if (!IsInsideTree() || _initialBlankOption == null)
            {
                return;
            }

            LoadCurrentValues();
            if (!_isHost)
            {
                _statusLabel.Text = Text("房主已更新设置。", "The host updated the settings.");
            }
        }).CallDeferred();
    }

    private void OnPretrainingChanged()
    {
        Callable.From(() =>
        {
            if (IsInsideTree())
            {
                RefreshPretrainingUi();
            }
        }).CallDeferred();
    }

    private static HBoxContainer CreateSettingRow(string title, Control input)
    {
        HBoxContainer row = new();
        row.CustomMinimumSize = new Vector2(0f, 48f);
        row.AddThemeConstantOverride("separation", 22);
        Label label = CreateLabel(title, 22);
        label.CustomMinimumSize = new Vector2(210f, 0f);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(label);
        input.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(input);
        return row;
    }

    private static OptionButton CreateOptionButton()
    {
        OptionButton option = new()
        {
            FocusMode = FocusModeEnum.All,
            CustomMinimumSize = new Vector2(300f, 46f)
        };
        option.AddThemeFontSizeOverride("font_size", 21);
        ApplyGameFont(option);
        return option;
    }

    private static CheckButton CreateToggle()
    {
        CheckButton toggle = new()
        {
            FocusMode = FocusModeEnum.All,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        toggle.AddThemeFontSizeOverride("font_size", 21);
        ApplyGameFont(toggle);
        return toggle;
    }

    private static void AddOption(OptionButton option, string text, int id)
    {
        option.AddItem(text, id);
    }

    private static void SelectById(OptionButton option, int id)
    {
        for (int index = 0; index < option.ItemCount; index++)
        {
            if (option.GetItemId(index) == id)
            {
                option.Select(index);
                return;
            }
        }
        option.Select(0);
    }

    private static Label CreateLabel(string text, int fontSize, FontType fontType = FontType.Regular)
    {
        Label label = new()
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", new Color("F4EEE0"));
        label.ApplyLocaleFontSubstitution(fontType, ThemeConstants.Label.Font);
        return label;
    }

    private static Button CreateButton(string text, bool primary)
    {
        Button button = new()
        {
            Text = text,
            CustomMinimumSize = new Vector2(130f, 46f),
            FocusMode = FocusModeEnum.All
        };
        Color fill = primary ? new Color("315D5D") : new Color("30373C");
        Color border = primary ? new Color("88D8CE") : new Color("778189");
        button.AddThemeFontSizeOverride("font_size", 21);
        ApplyGameFont(button);
        button.AddThemeColorOverride("font_color", Colors.White);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", Colors.White);
        button.AddThemeStyleboxOverride("normal", CreateButtonStyle(fill, border));
        button.AddThemeStyleboxOverride("hover", CreateButtonStyle(fill.Lightened(0.12f), border.Lightened(0.12f)));
        button.AddThemeStyleboxOverride("pressed", CreateButtonStyle(fill.Darkened(0.12f), border));
        return button;
    }

    private static void ApplyGameFont(Control control)
    {
        control.ApplyLocaleFontSubstitution(FontType.Regular, new StringName("font"));
    }

    private static StyleBoxFlat CreatePanelStyle()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.055f, 0.07f, 0.08f, 0.98f),
            BorderColor = new Color("70858D"),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ShadowColor = new Color(0f, 0f, 0f, 0.55f),
            ShadowSize = 18
        };
    }

    private static StyleBoxFlat CreateButtonStyle(Color fill, Color border)
    {
        return new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 14f,
            ContentMarginRight = 14f,
            ContentMarginTop = 8f,
            ContentMarginBottom = 8f
        };
    }

    private static void Close()
    {
        NModalContainer.Instance?.Clear();
    }

    private static string Text(string simplifiedChinese, string english)
    {
        return ModText.Get(simplifiedChinese, english);
    }
}
