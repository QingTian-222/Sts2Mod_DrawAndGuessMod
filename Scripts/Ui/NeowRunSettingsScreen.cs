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

        Label title = CreateLabel(Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.DRAWING_SETTINGS"), 34, FontType.Bold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        content.AddChild(title);

        string subtitleText = _isHost
            ? Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.CHANGES_ARE_APPLIED_AND_SYNCHRONIZED_TO_ALL")
            : Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.THE_HOST_CONTROLS_THESE_SETTINGS_THIS_VIEW");
        Label subtitle = CreateLabel(subtitleText, 20);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        subtitle.AddThemeColorOverride("font_color", new Color("B9C2C8"));
        content.AddChild(subtitle);
        content.AddChild(new HSeparator());

        _gameplayEnabledToggle = new CheckButton
        {
            FocusMode = FocusModeEnum.All,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.DISABLE_THIS_TO_REMOVE_BLANK_RELATED_EVENTS")
        };
        _gameplayEnabledToggle.AddThemeFontSizeOverride("font_size", 21);
        ApplyGameFont(_gameplayEnabledToggle);
        _gameplayEnabledToggle.Toggled += enabled =>
        {
            RefreshGameplayToggleText();
            SetRuleControlsEditable(_isHost && enabled);
        };
        content.AddChild(CreateSettingRow(
            Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.ENABLE_DRAW_GUESS_GAMEPLAY"),
            _gameplayEnabledToggle));

        _initialBlankOption = CreateOptionButton();
        AddOption(_initialBlankOption, Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.NONE"), (int)InitialBlankMode.None);
        AddOption(_initialBlankOption, Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.ONE_RANDOM_PLAYER"), (int)InitialBlankMode.RandomPlayer);
        AddOption(_initialBlankOption, Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.ALL_PLAYERS"), (int)InitialBlankMode.AllPlayers);
        content.AddChild(CreateSettingRow(Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.STARTING_BLANK"), _initialBlankOption));

        _drawingTimeOption = CreateOptionButton();
        AddOption(_drawingTimeOption, Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.UNLIMITED"), 0);
        AddOption(_drawingTimeOption, Text("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.30_SECONDS"), 30);
        AddOption(_drawingTimeOption, Text("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.60_SECONDS"), 60);
        AddOption(_drawingTimeOption, Text("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.90_SECONDS"), 90);
        AddOption(_drawingTimeOption, Text("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.120_SECONDS"), 120);
        content.AddChild(CreateSettingRow(Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.DRAWING_TIME"), _drawingTimeOption));

        _noRepeatCheck = CreateToggle();
        _noRepeatCheck.Toggled += _ => RefreshToggleText(_noRepeatCheck);
        content.AddChild(CreateSettingRow(Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.NO_REPEAT_CARDS"), _noRepeatCheck));

        _skipDeckCheck = CreateToggle();
        _skipDeckCheck.Toggled += _ => RefreshToggleText(_skipDeckCheck);
        content.AddChild(CreateSettingRow(Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.DO_NOT_ADD_DRAWN_CARDS_TO_DECK"), _skipDeckCheck));

        _cardRestrictionOption = CreateOptionButton();
        AddOption(_cardRestrictionOption, Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.UNLIMITED"), (int)DrawingCardRestriction.None);
        AddOption(_cardRestrictionOption, Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.NO_ANCIENT_CARDS"), (int)DrawingCardRestriction.ExcludeAncient);
        AddOption(_cardRestrictionOption, Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.OWN_CHARACTER_CARDS_ONLY"), (int)DrawingCardRestriction.CurrentCharacter);
        content.AddChild(CreateSettingRow(Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.CARD_RESTRICTION"), _cardRestrictionOption));

        _treasureRoomRelicDrawingCheck = CreateToggle();
        _treasureRoomRelicDrawingCheck.TooltipText = Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.IN_MULTIPLAYER_EACH_PLAYER_DRAWS_A_RELIC");
        _treasureRoomRelicDrawingCheck.Toggled += _ => RefreshToggleText(_treasureRoomRelicDrawingCheck);
        HBoxContainer treasureRoomRelicDrawingRow = CreateSettingRow(
            Text("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.DRAW_RELICS_IN_TREASURE_ROOMS"),
            _treasureRoomRelicDrawingCheck);
        treasureRoomRelicDrawingRow.Visible = _runState.Players.Count > 1;
        content.AddChild(treasureRoomRelicDrawingRow);
        _gameplayEnabledToggle.Disabled = !_isHost;
        SetRuleControlsEditable(_isHost && _gameplayEnabledToggle.ButtonPressed);

        content.AddChild(new HSeparator());
        HBoxContainer precacheHeader = new();
        Label precacheTitle = CreateLabel(Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.CARD_PRE_CACHE"), 22, FontType.Bold);
        precacheTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        precacheHeader.AddChild(precacheTitle);
        _precacheButton = CreateButton(Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.START_PRE_CACHE"), false);
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
        _closeButton = CreateButton(_isHost ? Text("DRAW_AND_GUESS_MOD.DRAWING_SCREEN.CANCEL") : Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.CLOSE"), false);
        _closeButton.Pressed += Close;
        actions.AddChild(_closeButton);
        _applyButton = CreateButton(Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.APPLY"), true);
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
            _statusLabel.Text = Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.FAILED_TO_APPLY") + ex.Message;
        }
    }

    private void RefreshGameplayToggleText()
    {
        RefreshToggleText(_gameplayEnabledToggle);
    }

    private static void RefreshToggleText(CheckButton toggle)
    {
        toggle.Text = toggle.ButtonPressed
            ? Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.ENABLED")
            : Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.DISABLED");
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
            ? Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.PRE_CACHING")
            : Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.START_PRE_CACHE");
        _precacheProgressBar.Value = DrawAndGuessSettings.PretrainingProgress;
        _precacheThumbnail.Texture = DrawAndGuessSettings.CurrentPretrainingThumbnail;
        _precacheThumbnail.Visible = running && _precacheThumbnail.Texture != null;
        _precacheProgressLabel.Text = string.IsNullOrWhiteSpace(DrawAndGuessSettings.CurrentPretrainingStatus)
            ? Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.PRE_CACHE_HAS_NOT_STARTED_YET")
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
                _statusLabel.Text = Text("DRAW_AND_GUESS_MOD.NEOW_RUN_SETTINGS_SCREEN.THE_HOST_UPDATED_THE_SETTINGS");
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

    private static string Text(string key)
    {
        return ModText.Get(key);
    }
}
