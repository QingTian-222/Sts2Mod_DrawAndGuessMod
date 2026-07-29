using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Ai;
using DrawAndGuessMod.Scripts.Config;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.Networking;
using DrawAndGuessMod.Scripts.State;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Ui;

public sealed record DrawingResult(byte[] PngBytes, CardGuess Guess, bool SkipAddingToDeck);
public sealed record RelicDrawingResult(
    byte[] PngBytes,
    RelicModel Relic,
    string WorkTitle);
public sealed record DrawingScreenOptions(
    string Title,
    string Help,
    double? TimeLimitSeconds = null,
    string? PeekTooltip = null,
    bool CloseWhenRestSiteEnds = false,
    GuessCandidateScope CandidateScope = GuessCandidateScope.Default,
    DrawingCanvasMode InitialCanvasMode = DrawingCanvasMode.Standard,
    bool AllowCanvasModeSwitch = true);

public partial class DrawingScreen : Control
{
    private const int MaxUndoSteps = 20;
    private const int CustomColorCapacity = DrawingPaletteStore.Capacity;
    private const float ColorButtonHeight = 30f;
    private const ulong TimerSyncIntervalMsec = 250uL;
    private const string PenNibIconPath =
        "res://images/atlases/relic_atlas.sprites/pen_nib.tres";
    private const string InkBottleIconPath =
        "res://images/atlases/relic_atlas.sprites/ink_bottle.tres";
    private static DrawingScreen? _active;
    private readonly TaskCompletionSource<DrawingResult?> _completion = new();
    private readonly TaskCompletionSource<RelicDrawingResult?> _relicCompletion = new();
    private readonly List<DrawingCommand> _pendingCommands = new();
    private readonly List<RecordedDrawingCommand> _historyCommands = new();
    private readonly LinkedList<DrawingOperationKey> _undoableOperations = new();
    private readonly Stack<DrawingOperationKey> _redoableOperations = new();
    private readonly HashSet<DrawingOperationKey> _completedOperations = new();
    private readonly HashSet<DrawingOperationKey> _undoneOperations = new();
    private readonly Dictionary<DrawingOperationKey, List<DrawingCommand>> _pendingMultiplayerOperations = new();
    private readonly CollaborativeDrawingHistory<DrawingPixelPatch> _multiplayerHistory = new(MaxUndoSteps);
    private readonly Dictionary<ulong, uint> _settledOperationWatermarks = new();
    private readonly List<Color> _customColors = new();
    private readonly List<Button> _customColorButtons = new();
    private Player _owner = null!;
    private Player _paletteOwner = null!;
    private DrawingScreenOptions? _options;
    private uint _sessionId;
    private uint _historyEpoch;
    private uint _canvasStateSequence;
    private bool _isChooser;
    private bool _isTimerAuthority;
    private bool _isRegularBlank;
    private bool _excludePreviouslySelectedBlankCards;
    private bool _receivedAuthoritativeBlankSettings;
    private ulong _lastCommandFlushMsec;
    private ulong _lastTimerSyncMsec;
    private DrawingCanvas _canvas = null!;
    private Label _status = null!;
    private Button _guessButton = null!;
    private Button _brushToolButton = null!;
    private Button _fillToolButton = null!;
    private Label _sizeLabel = null!;
    private HSlider _sizeSlider = null!;
    private Button _leftColorButton = null!;
    private Button _rightColorButton = null!;
    private Button _peekButton = null!;
    private EyeIconControl _peekIcon = null!;
    private Button _canvasModeButton = null!;
    private CanvasModeIconControl _canvasModeIcon = null!;
    private Control _peekBackdrop = null!;
    private Control _peekPanelContainer = null!;
    private Control _colorPickerOverlay = null!;
    private ColorPicker _colorPicker = null!;
    private Button _confirmColorButton = null!;
    private SpinBox _redInput = null!;
    private SpinBox _greenInput = null!;
    private SpinBox _blueInput = null!;
    private SpinBox _alphaInput = null!;
    private Tween? _peekTween;
    private Color _leftColor = new("1B1A18");
    private Color _rightColor = DrawingCanvas.PaperColor;
    private bool _syncingColorInputs;
    private bool _peeking;
    private bool _finishing;
    private bool _canvasModeLocked;
    private DrawingCanvasMode _canvasMode;
    private bool _gFillHeld;
    private bool _syncingSizeSlider;
    private bool _showingStampSize;
    private int _brushSize = DrawingCanvas.DefaultBrushSize;
    private int _stampSize = DrawingCanvas.DefaultStampSize;
    private double? _remainingSeconds;
    private double? _timerDurationSeconds;
    private CenterContainer? _timerCenter;
    private ProgressBar? _timerBar;
    private StyleBoxFlat? _timerFillStyle;
    private bool _receivedAuthoritativeTimer;
    private bool _privateDrawing;
    private RelicModel? _relicTarget;
    private RelicArtGuess? _currentRelicGuess;
    private TextureRect? _relicGuessImage;
    private TextureRect? _relicTargetImage;
    private Label? _relicWorkTitleLabel;
    private LineEdit? _relicWorkTitleInput;
    private Button? _relicWorkTitleEditButton;
    private bool _editingRelicWorkTitle;

    public static Task<DrawingResult?> ShowAsync(Player owner, uint sessionId, DrawingScreenOptions? options = null, double? defaultTimeLimitSeconds = null, bool isRegularBlank = false)
    {
        if (_active != null && GodotObject.IsInstanceValid(_active))
        {
            if (_active._owner.NetId == owner.NetId && _active._sessionId == sessionId)
            {
                return _active._completion.Task;
            }

            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Rejected drawing session {owner.NetId}/{sessionId} because " +
                $"session {_active._owner.NetId}/{_active._sessionId} is still active.");
            return Task.FromResult<DrawingResult?>(null);
        }

        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return Task.FromResult<DrawingResult?>(null);
        }

        bool isChooser = LocalContext.IsMe(owner);
        bool isTimerAuthority = !DrawingNetSync.IsMultiplayer || DrawingNetSync.IsLocalHost;
        bool hasAuthoritativeBlankSettings =
            !isRegularBlank || !DrawingNetSync.IsMultiplayer || isTimerAuthority;
        Player paletteOwner = LocalContext.GetMe(owner.RunState) ?? owner;
        double? requestedTimeLimit = options?.TimeLimitSeconds ?? defaultTimeLimitSeconds;
        double? effectiveTimeLimit = isTimerAuthority
            ? requestedTimeLimit
            : null;
        DrawingScreen screen = new()
        {
            Name = "DrawAndGuessMod_DrawingScreen",
            _owner = owner,
            _paletteOwner = paletteOwner,
            _sessionId = sessionId,
            _isChooser = isChooser,
            _isTimerAuthority = isTimerAuthority,
            _isRegularBlank = isRegularBlank,
            _excludePreviouslySelectedBlankCards = isRegularBlank &&
                                                       hasAuthoritativeBlankSettings &&
                                                       DrawAndGuessSettings.ExcludePreviouslySelectedBlankCards,
            _receivedAuthoritativeBlankSettings = hasAuthoritativeBlankSettings,
            _options = options,
            _canvasMode = options?.InitialCanvasMode ?? DrawingCanvasMode.Standard,
            _rightColor = options?.InitialCanvasMode == DrawingCanvasMode.Relic
                ? Colors.Transparent
                : DrawingCanvas.PaperColor,
            _remainingSeconds = effectiveTimeLimit,
            _timerDurationSeconds = effectiveTimeLimit
        };
        screen._customColors.AddRange(DrawingPaletteStore.GetColors(paletteOwner));
        DrawingNetSync.BeginSession(owner.NetId, sessionId);
        _active = screen;
        tree.Root.AddChild(screen);
        return screen._completion.Task;
    }

    public static Task<RelicDrawingResult?> ShowRelicAsync(Player owner, uint sessionId, RelicModel target, DrawingScreenOptions options)
    {
        if (_active != null && GodotObject.IsInstanceValid(_active))
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Rejected private relic drawing session {owner.NetId}/{sessionId} because another drawing is active.");
            return Task.FromResult<RelicDrawingResult?>(null);
        }

        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return Task.FromResult<RelicDrawingResult?>(null);
        }

        Player paletteOwner = LocalContext.GetMe(owner.RunState) ?? owner;
        DrawingScreen screen = new()
        {
            Name = "DrawAndGuessMod_RelicDrawingScreen",
            _owner = owner,
            _paletteOwner = paletteOwner,
            _sessionId = sessionId,
            _isChooser = true,
            _isTimerAuthority = true,
            _receivedAuthoritativeBlankSettings = true,
            _options = options with
            {
                InitialCanvasMode = DrawingCanvasMode.Relic,
                AllowCanvasModeSwitch = false
            },
            _canvasMode = DrawingCanvasMode.Relic,
            _rightColor = Colors.Transparent,
            _privateDrawing = true,
            _relicTarget = target
        };
        screen._customColors.AddRange(DrawingPaletteStore.GetColors(paletteOwner));
        _active = screen;
        tree.Root.AddChild(screen);
        return screen._relicCompletion.Task;
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 4000;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        BuildUi();
        if (!_privateDrawing)
        {
            DrawingNetSync.DeliverPending(this, _owner.NetId, _sessionId);
            SendBlankSettings();
            SendTimerSync(force: true);
        }
    }

    public override void _Process(double delta)
    {
        if (ShouldCloseForRunExit())
        {
            _finishing = true;
            _pendingCommands.Clear();
            Entry.Logger.Info(
                $"[DrawAndGuessMod] Closing drawing session {_sessionId} because the active run ended.");
            Complete(null);
            return;
        }

        if (_pendingCommands.Count > 0 && Time.GetTicksMsec() >= _lastCommandFlushMsec + 50)
        {
            FlushCommands();
        }

        if (_remainingSeconds is not double remaining || _finishing)
        {
            return;
        }

        if (!_isTimerAuthority)
        {
            return;
        }

        double previousRemaining = remaining;
        remaining = Math.Max(0d, remaining - delta);
        _remainingSeconds = remaining;
        UpdateTimerBar();
        SendTimerSync(force: previousRemaining > 0d && remaining <= 0d);
        if (remaining <= 0d && _isChooser)
        {
            OnGuessPressed();
        }
    }

    private bool ShouldCloseForRunExit()
    {
        RunManager runManager = RunManager.Instance;
        return runManager.IsAbandoned ||
               runManager.IsCleaningUp ||
               !runManager.IsInProgress ||
               !ReferenceEquals(runManager.DebugOnlyGetState(), _owner.RunState) ||
               (_options?.CloseWhenRestSiteEnds == true &&
                _owner.RunState.CurrentRoom is not RestSiteRoom);
    }

    public override void _Input(InputEvent @event)
    {
        if (_peeking)
        {
            return;
        }

        if (IsEditingRelicWorkTitle())
        {
            return;
        }

        if (@event is InputEventMouseButton { Pressed: true } wheel &&
            wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown &&
            !_finishing)
        {
            double direction = wheel.ButtonIndex == MouseButton.WheelUp ? 1d : -1d;
            _sizeSlider.Value = Math.Clamp(
                _sizeSlider.Value + direction * _sizeSlider.Step,
                _sizeSlider.MinValue,
                _sizeSlider.MaxValue);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventKey { Echo: false, Keycode: Key.G } fillKey)
        {
            if (fillKey.Pressed && !_gFillHeld && !_finishing && !_colorPickerOverlay.Visible)
            {
                _gFillHeld = true;
                ActivateFillTool();
                GetViewport().SetInputAsHandled();
                return;
            }
            if (!fillKey.Pressed && _gFillHeld)
            {
                _gFillHeld = false;
                ActivateBrushTool();
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (@event is InputEventKey
            {
                Pressed: true,
                Echo: false,
                CtrlPressed: true,
                ShiftPressed: false,
                Keycode: Key.Z
            } &&
            !_colorPickerOverlay.Visible)
        {
            RequestUndo();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventKey
            {
                Pressed: true,
                Echo: false,
                CtrlPressed: true
            } redoKey &&
            (redoKey.Keycode == Key.Y || redoKey is { ShiftPressed: true, Keycode: Key.Z }) &&
            !_colorPickerOverlay.Visible)
        {
            RequestRedo();
            GetViewport().SetInputAsHandled();
            return;
        }

    }

    public override void _ExitTree()
    {
        if (!_completion.Task.IsCompleted)
        {
            _completion.TrySetResult(null);
        }
        if (!_relicCompletion.Task.IsCompleted)
        {
            _relicCompletion.TrySetResult(null);
        }
        if (ReferenceEquals(_active, this))
        {
            _active = null;
        }
    }

    private void BuildUi()
    {
        ColorRect backdrop = new()
        {
            Color = new Color(0.015f, 0.02f, 0.03f, 0.9f),
            MouseFilter = MouseFilterEnum.Stop
        };
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        CenterContainer center = new();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        PanelContainer panel = new()
        {
            CustomMinimumSize = new Vector2(880f, 760f)
        };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        center.AddChild(panel);

        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_right", 28);
        margin.AddThemeConstantOverride("margin_top", 22);
        margin.AddThemeConstantOverride("margin_bottom", 22);
        panel.AddChild(margin);

        VBoxContainer column = new();
        column.AddThemeConstantOverride("separation", 12);
        margin.AddChild(column);

        if (_relicTarget != null)
        {
            BuildRelicTitleEditor(column);
        }
        else
        {
            Label title = new()
            {
                Text = _options?.Title ?? Localized("空白", "Blank"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            title.AddThemeFontSizeOverride("font_size", 30);
            column.AddChild(title);
        }

        Label help = new()
        {
            Text = _options?.Help ?? (DrawingNetSync.IsMultiplayer
                ? Localized(
                    "所有玩家都可以共同作画；出牌者负责确认，被指定的玩家进行三选一。",
                    "Everyone can draw together. The player who played the card confirms the drawing, and the targeted player chooses from three cards.")
                : Localized(
                    "绘制卡面后，让瓦库给出三个候选。",
                    "Draw a card illustration and let VAKUU suggest three candidates.")),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        column.AddChild(help);

        _timerCenter = new CenterContainer
        {
            Visible = false
        };
        column.AddChild(_timerCenter);
        if (_remainingSeconds.HasValue)
        {
            EnsureTimerBar(_timerDurationSeconds ?? _remainingSeconds.Value);
        }

        HBoxContainer canvasRow = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        canvasRow.AddThemeConstantOverride("separation", 12);
        column.AddChild(canvasRow);

        _canvas = new DrawingCanvas();
        _canvas.SetCanvasMode(_canvasMode);
        _canvas.LocalCommandGenerated += OnLocalCommand;
        _canvas.LeftColorSampled += OnLeftColorSampled;
        _canvas.SetMouseColors(_leftColor, _rightColor);
        if (_relicTarget != null)
        {
            canvasRow.AddChild(new Control
            {
                CustomMinimumSize = new Vector2(230f, 0f),
                MouseFilter = MouseFilterEnum.Ignore
            });
            _canvas.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            canvasRow.AddChild(_canvas);
            BuildRelicReferencePanel(canvasRow);
        }
        else
        {
            CenterContainer canvasCenter = new()
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            canvasRow.AddChild(canvasCenter);
            canvasCenter.AddChild(_canvas);
        }

        HBoxContainer paletteArea = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center
        };
        paletteArea.AddThemeConstantOverride("separation", 8);
        column.AddChild(paletteArea);

        VBoxContainer colorAssignments = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center
        };
        colorAssignments.AddThemeConstantOverride("separation", 4);
        paletteArea.AddChild(colorAssignments);
        _leftColorButton = CreateMouseColorButton(
            Localized("左键", "LMB"),
            Localized(
                "当前左键颜色；左键点击右侧色块即可替换",
                "Current left mouse button color; left-click a swatch to replace it"));
        _rightColorButton = CreateMouseColorButton(
            Localized("右键", "RMB"),
            Localized(
                "当前右键颜色；右键点击右侧色块即可替换",
                "Current right mouse button color; right-click a swatch to replace it"));
        colorAssignments.AddChild(_leftColorButton);
        colorAssignments.AddChild(_rightColorButton);

        VBoxContainer paletteRows = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center
        };
        paletteRows.AddThemeConstantOverride("separation", 4);
        paletteArea.AddChild(paletteRows);

        HBoxContainer fixedColorsTop = CreateColorRow();
        HBoxContainer fixedColorsBottom = CreateColorRow();
        paletteRows.AddChild(fixedColorsTop);
        paletteRows.AddChild(fixedColorsBottom);
        for (int index = 0; index < BrushPalette.Entries.Count; index++)
        {
            BrushPaletteEntry entry = BrushPalette.Entries[index];
            AddBrushButton(index < 12 ? fixedColorsTop : fixedColorsBottom, entry.Name, entry.Color);
        }

        HBoxContainer customColors = CreateColorRow();
        paletteRows.AddChild(customColors);
        Button openColorPicker = new()
        {
            Text = "🎨",
            TooltipText = Localized(
                "调色盘：从色图选择或输入 RGBA",
                "Color palette: choose from the color field or enter RGBA values"),
            CustomMinimumSize = new Vector2(30f, ColorButtonHeight),
            ClipText = true,
            FocusMode = FocusModeEnum.None
        };
        openColorPicker.AddThemeFontSizeOverride("font_size", 12);
        openColorPicker.Pressed += OpenColorPicker;
        customColors.AddChild(openColorPicker);
        for (int slot = 0; slot < CustomColorCapacity; slot++)
        {
            int slotIndex = slot;
            Button customColor = new()
            {
                Disabled = true,
                TooltipText = Localized("空白自定义颜色", "Empty custom color slot"),
                CustomMinimumSize = new Vector2(30f, ColorButtonHeight),
                FocusMode = FocusModeEnum.None
            };
            customColor.Pressed += () => SelectCustomColor(slotIndex);
            customColor.GuiInput += @event =>
            {
                if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } &&
                    slotIndex < _customColors.Count)
                {
                    SelectRightColor(_customColors[slotIndex]);
                    customColor.AcceptEvent();
                }
            };
            _customColorButtons.Add(customColor);
            customColors.AddChild(customColor);
        }
        RefreshCustomColorButtons();
        RefreshMouseColorButtons();

        HBoxContainer tools = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center
        };
        tools.AddThemeConstantOverride("separation", 8);
        column.AddChild(tools);

        VBoxContainer drawingTools = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            CustomMinimumSize = new Vector2(96f, 0f)
        };
        drawingTools.AddThemeConstantOverride("separation", 6);
        tools.AddChild(drawingTools);

        ButtonGroup drawingToolGroup = new() { AllowUnpress = true };
        _brushToolButton = CreateDrawingToolButton(
            ResourceLoader.Load<Texture2D>(PenNibIconPath, null, ResourceLoader.CacheMode.Reuse),
            Localized("画笔", "Brush"),
            Localized(
                "使用左键或右键颜色绘制",
                "Draw with the left or right mouse button color"),
            drawingToolGroup);
        _fillToolButton = CreateDrawingToolButton(
            ResourceLoader.Load<Texture2D>(InkBottleIconPath, null, ResourceLoader.CacheMode.Reuse),
            Localized("填充", "Fill"),
            Localized(
                "快捷键：按住 G 临时切换，松开回到画笔\n",
                "Shortcut: hold G temporarily and release to return to the brush\n"),
            drawingToolGroup);
        _brushToolButton.Pressed += ActivateBrushTool;
        _fillToolButton.Pressed += ActivateFillTool;
        drawingTools.AddChild(_brushToolButton);
        drawingTools.AddChild(_fillToolButton);
        SelectDrawingTool(_brushToolButton);

        VBoxContainer toolOptions = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center
        };
        toolOptions.AddThemeConstantOverride("separation", 10);
        tools.AddChild(toolOptions);

        HBoxContainer sizeRow = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center
        };
        sizeRow.AddThemeConstantOverride("separation", 8);
        toolOptions.AddChild(sizeRow);

        _sizeLabel = new Label
        {
            Text = Localized(
                $"粗细：{DrawingCanvas.DefaultBrushSize} px",
                $"Size: {DrawingCanvas.DefaultBrushSize} px"),
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(92f, 0f)
        };
        sizeRow.AddChild(_sizeLabel);

        _sizeSlider = new HSlider
        {
            MinValue = DrawingCanvas.MinBrushSize,
            MaxValue = DrawingCanvas.MaxBrushSize,
            Step = 1d,
            Value = DrawingCanvas.DefaultBrushSize,
            CustomMinimumSize = new Vector2(170f, 32f),
            TooltipText = Localized(
                "调整画笔粗细",
                "Adjust brush size")
        };
        _sizeSlider.ValueChanged += value =>
        {
            if (_syncingSizeSlider)
            {
                return;
            }

            int size = Mathf.RoundToInt(value);
            if (_showingStampSize)
            {
                _stampSize = size;
                _canvas.SetStampSize(size);
                _sizeLabel.Text = Localized($"印花：{size} px", $"Stamp: {size} px");
            }
            else
            {
                _brushSize = size;
                _canvas.SetBrushSize(size);
                _sizeLabel.Text = Localized($"粗细：{size} px", $"Size: {size} px");
            }
        };
        sizeRow.AddChild(_sizeSlider);

        HBoxContainer stampRow = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center
        };
        stampRow.AddThemeConstantOverride("separation", 8);
        toolOptions.AddChild(stampRow);

        Label stampLabel = new()
        {
            Text = Localized("角色印花：", "Character stamps:"),
            VerticalAlignment = VerticalAlignment.Center
        };
        stampRow.AddChild(stampLabel);
        AddStampButton(stampRow, ModelDb.Character<Ironclad>(), 0);
        AddStampButton(stampRow, ModelDb.Character<Silent>(), 1);
        AddStampButton(stampRow, ModelDb.Character<Defect>(), 2);
        AddStampButton(stampRow, ModelDb.Character<Necrobinder>(), 3);
        AddStampButton(stampRow, ModelDb.Character<Regent>(), 4);

        _status = new Label
        {
            Text = _relicTarget != null
                ? Localized("当前识别：尚未作画", "Current guess: no drawing yet")
                : _isChooser
                ? Localized("完成后由你确认。", "Confirm the drawing when everyone is finished.")
                : Localized(
                    "正在共同绘制，等待出牌者确认。",
                    "Drawing together. Waiting for the player who played the card to confirm."),
            Visible = _relicTarget != null,
            MouseFilter = MouseFilterEnum.Ignore
        };
        column.AddChild(_status);

        HBoxContainer buttons = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center
        };
        buttons.AddThemeConstantOverride("separation", 12);
        column.AddChild(buttons);

        Button clear = CreateActionButton(
            Localized("清空", "Clear"),
            new Color("5B252B"),
            new Color("E47A78"));
        clear.Pressed += _canvas.ClearCanvas;
        buttons.AddChild(clear);

        Button undo = CreateActionButton(
            Localized("撤回", "Undo"),
            new Color("253D58"),
            new Color("79BCE8"));
        undo.TooltipText = DrawingNetSync.IsMultiplayer
            ? Localized(
                $"撤回你自己的最近一次完整操作（Ctrl+Z；Ctrl+Y 重做），最多保留 {MaxUndoSteps} 步",
                $"Undo your most recent complete action (Ctrl+Z; Ctrl+Y to redo); up to {MaxUndoSteps} actions are retained")
            : Localized(
                $"撤回最近一次完整操作（Ctrl+Z；Ctrl+Y 重做），最多保留 {MaxUndoSteps} 步",
                $"Undo the most recent complete action (Ctrl+Z; Ctrl+Y to redo); up to {MaxUndoSteps} actions are retained");
        undo.Pressed += RequestUndo;
        buttons.AddChild(undo);

        _guessButton = CreateActionButton(
            Localized("确认", "Confirm"),
            new Color("176B72"),
            new Color("75F0E6"),
            primary: true);
        _guessButton.Disabled = !_isChooser ||
                                !_receivedAuthoritativeBlankSettings ||
                                _relicTarget != null;
        _guessButton.Pressed += OnGuessPressed;
        buttons.AddChild(_guessButton);

        AddPeekButton(backdrop, center);
        AddCanvasModeButton();
        BuildColorPickerOverlay();
    }

    private static Button CreateActionButton(string text, Color fill, Color border, bool primary = false)
    {
        Button button = new()
        {
            Text = text,
            FocusMode = FocusModeEnum.None
        };
        button.AddThemeColorOverride("font_color", Colors.White);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", Colors.White);
        button.AddThemeStyleboxOverride(
            "normal",
            CreateActionButtonStyle(fill, border, primary ? 3 : 2, primary ? 7 : 4));
        button.AddThemeStyleboxOverride(
            "hover",
            CreateActionButtonStyle(fill.Lightened(0.14f), border.Lightened(0.12f), 3, primary ? 9 : 6));
        button.AddThemeStyleboxOverride(
            "pressed",
            CreateActionButtonStyle(fill.Darkened(0.12f), border, 3, 2));
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        return button;
    }

    private static StyleBoxFlat CreateActionButtonStyle(Color fill, Color border, int borderWidth, int shadowSize)
    {
        return new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            ContentMarginLeft = 10f,
            ContentMarginRight = 10f,
            ContentMarginTop = 5f,
            ContentMarginBottom = 5f,
            ShadowColor = new Color(0f, 0f, 0f, 0.45f),
            ShadowSize = shadowSize,
            ShadowOffset = new Vector2(0f, 3f)
        };
    }

    private void AddPeekButton(Control backdrop, Control panelContainer)
    {
        _peekBackdrop = backdrop;
        _peekPanelContainer = panelContainer;
        _peekButton = new Button
        {
            Name = "PeekButton",
            Text = string.Empty,
            TooltipText = _options?.PeekTooltip ?? Localized("观察战局", "View combat"),
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Stop
        };
        _peekButton.SetAnchorsPreset(LayoutPreset.Center);
        _peekButton.OffsetLeft = -516f;
        _peekButton.OffsetTop = -32f;
        _peekButton.OffsetRight = -452f;
        _peekButton.OffsetBottom = 32f;
        _peekButton.AddThemeStyleboxOverride("normal", CreatePeekButtonStyle(new Color("171C24"), new Color("768393")));
        _peekButton.AddThemeStyleboxOverride("hover", CreatePeekButtonStyle(new Color("22303A"), new Color("8EE9E0")));
        _peekButton.AddThemeStyleboxOverride("pressed", CreatePeekButtonStyle(new Color("10272A"), new Color("75F0E6")));
        _peekButton.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());

        _peekIcon = new EyeIconControl
        {
            Name = "EyeIcon",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _peekIcon.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _peekButton.AddChild(_peekIcon);
        _peekButton.Pressed += () => SetPeeking(!_peeking);
        AddChild(_peekButton);
    }

    private void AddCanvasModeButton()
    {
        _canvasModeButton = new Button
        {
            Name = "CanvasModeButton",
            Text = string.Empty,
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Stop
        };
        _canvasModeButton.SetAnchorsPreset(LayoutPreset.Center);
        _canvasModeButton.OffsetLeft = 452f;
        _canvasModeButton.OffsetTop = -32f;
        _canvasModeButton.OffsetRight = 516f;
        _canvasModeButton.OffsetBottom = 32f;
        _canvasModeButton.AddThemeStyleboxOverride("normal", CreatePeekButtonStyle(new Color("171C24"), new Color("768393")));
        _canvasModeButton.AddThemeStyleboxOverride("hover", CreatePeekButtonStyle(new Color("22303A"), new Color("8EE9E0")));
        _canvasModeButton.AddThemeStyleboxOverride("pressed", CreatePeekButtonStyle(new Color("10272A"), new Color("75F0E6")));
        _canvasModeButton.AddThemeStyleboxOverride("disabled", CreatePeekButtonStyle(new Color("11151B"), new Color("46505B")));
        _canvasModeButton.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());

        _canvasModeIcon = new CanvasModeIconControl
        {
            Name = "CanvasModeIcon",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _canvasModeIcon.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _canvasModeButton.AddChild(_canvasModeIcon);
        _canvasModeButton.Pressed += SwitchCanvasMode;
        AddChild(_canvasModeButton);
        RefreshCanvasModeButton();
    }

    private static StyleBoxFlat CreatePeekButtonStyle(Color fill, Color border)
    {
        return new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border,
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10
        };
    }

    private void SetPeeking(bool peeking)
    {
        if (_peeking == peeking)
        {
            return;
        }

        if (peeking && _gFillHeld)
        {
            _gFillHeld = false;
            ActivateBrushTool();
        }

        _peeking = peeking;
        MouseFilter = peeking ? MouseFilterEnum.Ignore : MouseFilterEnum.Stop;
        _peekButton.MouseFilter = MouseFilterEnum.Stop;
        _canvasModeButton.Visible = !peeking;
        _peekBackdrop.Visible = !peeking;
        _peekPanelContainer.Visible = !peeking;
        _peekButton.TooltipText = peeking
            ? Localized("返回绘画", "Return to drawing")
            : _options?.PeekTooltip ?? Localized("观察战局", "View combat");
        _peekIcon.SetActive(peeking);
        AnimatePeekButton();
    }

    private void SwitchCanvasMode()
    {
        if (_finishing ||
            !_isChooser ||
            _options?.AllowCanvasModeSwitch == false ||
            _canvasModeLocked)
        {
            return;
        }

        FlushCommands();
        _canvasMode = _canvasMode == DrawingCanvasMode.Standard
            ? DrawingCanvasMode.Ancient
            : DrawingCanvasMode.Standard;
        _canvas.SetCanvasMode(_canvasMode);
        ResetDrawingHistory();
        AdvanceHistoryEpoch();
        _canvasStateSequence++;
        RefreshCanvasModeButton();
        SendAuthoritativeCanvasState(_canvas.ExportPng(), resetPendingOperations: true);
        Entry.Logger.Info(
            $"[DrawAndGuessMod] Switched drawing canvas mode to {_canvasMode}: " +
            $"owner={_owner.NetId}, session={_sessionId}, epoch={_historyEpoch}.");
    }

    private void RefreshCanvasModeButton()
    {
        if (_canvasModeButton == null)
        {
            return;
        }

        bool switchAllowed = _isChooser &&
                             _options?.AllowCanvasModeSwitch != false &&
                             !_canvasModeLocked &&
                             !_finishing;
        _canvasModeButton.Disabled = !switchAllowed;
        _canvasModeButton.TooltipText = Localized("切换画布", "Switch canvas");
        _canvasModeIcon.SetMode(_canvasMode, !switchAllowed);
    }

    private void AnimatePeekButton()
    {
        _peekTween?.Kill();
        _peekButton.PivotOffset = _peekButton.Size * 0.5f;
        _peekTween = CreateTween();
        _peekTween.TweenProperty(_peekButton, "scale", Vector2.One * 0.90f, 0.05d)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.Out);
        _peekTween.TweenProperty(_peekButton, "scale", Vector2.One, 0.14d)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);
    }

    private async void OnGuessPressed()
    {
        if (_finishing || !_receivedAuthoritativeBlankSettings)
        {
            return;
        }

        _finishing = true;
        _guessButton.Disabled = true;
        RefreshCanvasModeButton();
        _status.Text = "";
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        try
        {
            if (_relicTarget != null)
            {
                RelicArtGuess? finalGuess = RelicArtClassifier.GuessTopOne(_canvas.Snapshot());
                if (finalGuess?.Relic.Id != _relicTarget.Id)
                {
                    _currentRelicGuess = finalGuess;
                    UpdateRelicGuessStatus();
                    _finishing = false;
                    RefreshCanvasModeButton();
                    return;
                }

                byte[] relicPng = _canvas.ExportPng();
                await ToSignal(GetTree().CreateTimer(0.15d, processAlways: true), SceneTreeTimer.SignalName.Timeout);
                string workTitle = GetRelicWorkTitle();
                CompleteRelic(new RelicDrawingResult(
                    relicPng,
                    _relicTarget,
                    workTitle));
                return;
            }

            byte[] png = _canvas.ExportPng();
            IReadOnlySet<ModelId>? excludedCardIds =
                _isRegularBlank && _excludePreviouslySelectedBlankCards
                    ? BlankSelectionStore.GetSelectedCardIds(_owner.RunState)
                    : null;
            CardGuess guess = CardArtClassifier.Guess(
                _canvas.Snapshot(),
                _owner,
                _options?.CandidateScope ?? GuessCandidateScope.Default,
                excludedCardIds);
            bool skipAddingToDeck = DrawAndGuessSettings.BlankGeneratedCardSkipsDeck;
            _status.Text = "";
            FlushCommands();
            DrawingNetSync.SendFinal(new DrawingFinalMessage
            {
                OwnerId = _owner.NetId,
                SessionId = _sessionId,
                SkipAddingToDeck = skipAddingToDeck,
                CanvasMode = _canvasMode,
                CardIds = guess.NearestCards.Select(card => card.Id.Entry).ToList(),
                PngBytes = png
            });
            await ToSignal(GetTree().CreateTimer(0.6d, processAlways: true), SceneTreeTimer.SignalName.Timeout);
            Complete(new DrawingResult(
                png,
                guess,
                skipAddingToDeck));
        }
        catch (Exception ex)
        {
            Entry.Logger.Error($"[DrawAndGuessMod] Guess failed: {ex}");
            _status.Text = Localized("识别失败：", "Recognition failed: ") + ex.Message;
            _guessButton.Disabled = false;
            _finishing = false;
            RefreshCanvasModeButton();
        }
    }

    private void UpdateTimerBar()
    {
        if (_timerBar == null || _timerFillStyle == null || !_remainingSeconds.HasValue)
        {
            return;
        }

        double duration = Math.Max(0.001d, _timerDurationSeconds ?? 1d);
        float ratio = Mathf.Clamp((float)(_remainingSeconds.Value / duration), 0f, 1f);
        Color red = new("F05A55");
        Color yellow = new("F4C95D");
        Color green = new("58D68D");
        _timerBar.Value = _remainingSeconds.Value;
        _timerFillStyle.BgColor = ratio < 0.5f
            ? red.Lerp(yellow, ratio * 2f)
            : yellow.Lerp(green, (ratio - 0.5f) * 2f);
    }

    private void EnsureTimerBar(double duration)
    {
        if (_timerCenter == null)
        {
            return;
        }

        _timerDurationSeconds = Math.Max(0.001d, duration);
        if (_timerBar == null)
        {
            _timerFillStyle = CreateTimerBarStyle(new Color("58D68D"), Colors.Transparent, 0);
            _timerBar = new ProgressBar
            {
                MinValue = 0d,
                ShowPercentage = false,
                CustomMinimumSize = new Vector2(500f, 12f),
                MouseFilter = MouseFilterEnum.Ignore
            };
            _timerBar.AddThemeStyleboxOverride(
                "background",
                CreateTimerBarStyle(new Color("111722"), new Color("526070"), 2));
            _timerBar.AddThemeStyleboxOverride("fill", _timerFillStyle);
            _timerCenter.AddChild(_timerBar);
        }

        _timerBar.MaxValue = _timerDurationSeconds.Value;
        _timerCenter.Visible = true;
        UpdateTimerBar();
    }

    private static StyleBoxFlat CreateTimerBarStyle(Color fill, Color border, int borderWidth)
    {
        return new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4
        };
    }

    private static HBoxContainer CreateColorRow()
    {
        HBoxContainer row = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            CustomMinimumSize = new Vector2(0f, ColorButtonHeight)
        };
        row.AddThemeConstantOverride("separation", 4);
        return row;
    }

    private void AddBrushButton(Container palette, string colorName, Color color)
    {
        Button button = new()
        {
            Text = string.Empty,
            TooltipText = colorName + Localized(
                "\n右键：设为右键颜色",
                "\nRight-click: set as the right mouse button color"),
            CustomMinimumSize = new Vector2(30f, ColorButtonHeight),
            FocusMode = FocusModeEnum.None
        };
        ApplyColorButtonStyle(button, color);
        button.Pressed += () => SelectColor(color);
        button.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
            {
                SelectRightColor(color);
                button.AcceptEvent();
            }
        };
        palette.AddChild(button);
    }

    private void SelectCustomColor(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= _customColors.Count)
        {
            return;
        }

        Color color = _customColors[slotIndex];
        SelectColor(color);
    }

    private void SelectColor(Color color)
    {
        _leftColor = DrawingCommand.UnpackRgb(DrawingCommand.PackRgb(color));
        _canvas.SetMouseColors(_leftColor, _rightColor);
        RefreshMouseColorButtons();
        if (_canvas.IsStampTool())
        {
            ActivateBrushTool();
        }
    }

    private void SelectRightColor(Color color)
    {
        _rightColor = DrawingCommand.UnpackRgb(DrawingCommand.PackRgb(color));
        _canvas.SetMouseColors(_leftColor, _rightColor);
        RefreshMouseColorButtons();
        if (_canvas.IsStampTool())
        {
            ActivateBrushTool();
        }
    }

    private void OnLeftColorSampled(Color color)
    {
        _leftColor = DrawingCommand.UnpackRgb(DrawingCommand.PackRgb(color));
        _canvas.SetMouseColors(_leftColor, _rightColor);
        RefreshMouseColorButtons();
    }

    private static Button CreateMouseColorButton(string text, string tooltip)
    {
        return new Button
        {
            Text = text,
            TooltipText = tooltip,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(66f, 47f)
        };
    }

    private void RefreshMouseColorButtons()
    {
        ApplyMouseColorButtonStyle(_leftColorButton, _leftColor);
        ApplyMouseColorButtonStyle(_rightColorButton, _rightColor);
    }

    private static void ApplyMouseColorButtonStyle(Button button, Color color)
    {
        float luminance = color.R * 0.2126f + color.G * 0.7152f + color.B * 0.0722f;
        Color foreground = luminance > 0.55f ? new Color("171B20") : new Color("FFF8E8");
        Color border = new("8C938F");
        button.AddThemeFontSizeOverride("font_size", 15);
        button.AddThemeColorOverride("font_color", foreground);
        button.AddThemeColorOverride("font_hover_color", foreground);
        button.AddThemeColorOverride("font_pressed_color", foreground);
        button.AddThemeColorOverride("font_hover_pressed_color", foreground);
        button.AddThemeStyleboxOverride("normal", CreateMouseColorButtonStyle(color, border, 2));
        button.AddThemeStyleboxOverride("hover", CreateMouseColorButtonStyle(color, border, 2));
        button.AddThemeStyleboxOverride("pressed", CreateMouseColorButtonStyle(color, border, 2));
    }

    private static StyleBoxFlat CreateMouseColorButtonStyle(Color fill, Color border, int borderWidth)
    {
        return new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = 7,
            CornerRadiusTopRight = 7,
            CornerRadiusBottomLeft = 7,
            CornerRadiusBottomRight = 7
        };
    }

    private static void ApplyColorButtonStyle(Button button, Color color)
    {
        StyleBoxFlat normal = CreateColorButtonStyle(color, new Color("CDC5B4"));
        StyleBoxFlat hover = CreateColorButtonStyle(color.Lightened(0.12f), new Color("75F0E6"));
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", hover);
        button.AddThemeStyleboxOverride("disabled", normal);
    }

    private static StyleBoxFlat CreateColorButtonStyle(Color fill, Color border)
    {
        return new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border,
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6
        };
    }

    private void RefreshCustomColorButtons()
    {
        for (int index = 0; index < _customColorButtons.Count; index++)
        {
            Button button = _customColorButtons[index];
            if (index < _customColors.Count)
            {
                Color color = _customColors[index];
                button.Disabled = false;
                button.TooltipText =
                    Localized("自定义 #", "Custom #") +
                    color.ToHtml(true).ToUpperInvariant() +
                    Localized(
                        "\n右键：设为右键颜色",
                        "\nRight-click: set as the right mouse button color");
                ApplyColorButtonStyle(button, color);
            }
            else
            {
                button.Disabled = true;
                button.TooltipText = Localized("空白自定义颜色", "Empty custom color slot");
                ApplyColorButtonStyle(button, new Color("2B3038"));
            }
        }
    }

    private void BuildColorPickerOverlay()
    {
        _colorPickerOverlay = new Control
        {
            Name = "ColorPickerOverlay",
            Visible = false,
            MouseFilter = MouseFilterEnum.Stop,
            ZIndex = 20
        };
        _colorPickerOverlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_colorPickerOverlay);

        ColorRect shade = new()
        {
            Color = new Color(0f, 0f, 0f, 0.78f),
            MouseFilter = MouseFilterEnum.Stop
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _colorPickerOverlay.AddChild(shade);

        CenterContainer center = new();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _colorPickerOverlay.AddChild(center);

        PanelContainer panel = new()
        {
            CustomMinimumSize = new Vector2(580f, 440f)
        };
        center.AddChild(panel);

        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 18);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        panel.AddChild(margin);

        VBoxContainer content = new();
        content.AddThemeConstantOverride("separation", 12);
        margin.AddChild(content);

        Label title = new()
        {
            Text = Localized("添加自定义颜色", "Add Custom Color"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 24);
        content.AddChild(title);

        HBoxContainer pickerBody = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        pickerBody.AddThemeConstantOverride("separation", 6);
        content.AddChild(pickerBody);

        _colorPicker = new ColorPicker
        {
            EditAlpha = true,
            EditIntensity = false,
            PickerShape = ColorPicker.PickerShapeType.HsvRectangle,
            CanAddSwatches = false,
            SamplerVisible = false,
            ColorModesVisible = false,
            SlidersVisible = false,
            HexVisible = false,
            PresetsVisible = false,
            Color = _leftColor,
            CustomMinimumSize = new Vector2(370f, 340f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _colorPicker.ColorChanged += OnColorPickerChanged;
        pickerBody.AddChild(_colorPicker);

        VBoxContainer rgbColumn = new()
        {
            CustomMinimumSize = new Vector2(160f, 0f),
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        rgbColumn.AddThemeConstantOverride("separation", 9);
        pickerBody.AddChild(rgbColumn);
        Label rgbTitle = new()
        {
            Text = "RGBA",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        rgbTitle.AddThemeFontSizeOverride("font_size", 20);
        rgbColumn.AddChild(rgbTitle);
        _redInput = AddRgbInput(rgbColumn, "R");
        _greenInput = AddRgbInput(rgbColumn, "G");
        _blueInput = AddRgbInput(rgbColumn, "B");
        _alphaInput = AddRgbInput(rgbColumn, "A");
        _redInput.ValueChanged += OnRgbInputChanged;
        _greenInput.ValueChanged += OnRgbInputChanged;
        _blueInput.ValueChanged += OnRgbInputChanged;
        _alphaInput.ValueChanged += OnRgbInputChanged;

        Control spacer = new()
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        rgbColumn.AddChild(spacer);
        Button cancel = new()
        {
            Text = Localized("取消", "Cancel"),
            CustomMinimumSize = new Vector2(150f, 50f)
        };
        ApplyColorPickerCancelStyle(cancel);
        cancel.Pressed += CloseColorPicker;
        rgbColumn.AddChild(cancel);
        _confirmColorButton = new Button
        {
            Text = Localized("确认添加", "Add Color"),
            CustomMinimumSize = new Vector2(150f, 50f)
        };
        UpdateConfirmColorButton(_leftColor);
        _confirmColorButton.Pressed += ConfirmCustomColor;
        rgbColumn.AddChild(_confirmColorButton);
    }

    private static void ApplyColorPickerCancelStyle(Button button)
    {
        Color normalFill = new("50343A");
        Color hoverFill = new("71434A");
        Color pressedFill = new("3A252A");
        Color border = new("E4AAA5");
        button.AddThemeFontSizeOverride("font_size", 19);
        button.AddThemeColorOverride("font_color", Colors.White);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", Colors.White);
        button.AddThemeStyleboxOverride("normal", CreateColorPickerActionStyle(normalFill, border, 2));
        button.AddThemeStyleboxOverride("hover", CreateColorPickerActionStyle(hoverFill, border.Lightened(0.12f), 3));
        button.AddThemeStyleboxOverride("pressed", CreateColorPickerActionStyle(pressedFill, border, 3));
        button.AddThemeStyleboxOverride("focus", CreateColorPickerActionStyle(hoverFill, border.Lightened(0.12f), 3));
    }

    private static StyleBoxFlat CreateColorPickerActionStyle(Color fill, Color border, int borderWidth)
    {
        return new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8
        };
    }

    private static SpinBox AddRgbInput(Container parent, string channel)
    {
        HBoxContainer row = new()
        {
            Alignment = BoxContainer.AlignmentMode.Begin
        };
        row.AddThemeConstantOverride("separation", 8);
        parent.AddChild(row);
        Label label = new()
        {
            Text = channel,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(18f, 0f)
        };
        row.AddChild(label);
        SpinBox input = new()
        {
            MinValue = 0d,
            MaxValue = 255d,
            Step = 1d,
            Rounded = true,
            CustomMinimumSize = new Vector2(108f, 34f)
        };
        row.AddChild(input);
        return input;
    }

    private void OpenColorPicker()
    {
        SyncColorInputs(_leftColor);
        _colorPickerOverlay.Visible = true;
    }

    private void CloseColorPicker()
    {
        _colorPickerOverlay.Visible = false;
    }

    private void OnColorPickerChanged(Color color)
    {
        if (_syncingColorInputs)
        {
            return;
        }
        SyncColorInputs(color);
    }

    private void OnRgbInputChanged(double value)
    {
        if (_syncingColorInputs)
        {
            return;
        }

        _syncingColorInputs = true;
        Color color = new(
            (float)(_redInput.Value / 255d),
            (float)(_greenInput.Value / 255d),
            (float)(_blueInput.Value / 255d),
            (float)(_alphaInput.Value / 255d));
        _colorPicker.Color = color;
        UpdateConfirmColorButton(color);
        _syncingColorInputs = false;
    }

    private void SyncColorInputs(Color color)
    {
        _syncingColorInputs = true;
        Color normalized = DrawingCommand.UnpackRgb(DrawingCommand.PackRgb(color));
        _colorPicker.Color = normalized;
        _redInput.Value = Mathf.RoundToInt(normalized.R * 255f);
        _greenInput.Value = Mathf.RoundToInt(normalized.G * 255f);
        _blueInput.Value = Mathf.RoundToInt(normalized.B * 255f);
        _alphaInput.Value = Mathf.RoundToInt(normalized.A * 255f);
        UpdateConfirmColorButton(normalized);
        _syncingColorInputs = false;
    }

    private void UpdateConfirmColorButton(Color color)
    {
        Color normalized = DrawingCommand.UnpackRgb(DrawingCommand.PackRgb(color));
        float luminance = normalized.R * 0.2126f + normalized.G * 0.7152f + normalized.B * 0.0722f;
        bool lightColor = luminance > 0.55f;
        Color foreground = lightColor ? new Color("171B20") : new Color("FFF8E8");
        Color hoverFill = lightColor ? normalized.Darkened(0.08f) : normalized.Lightened(0.10f);
        Color pressedFill = lightColor ? normalized.Darkened(0.16f) : normalized.Lightened(0.18f);
        _confirmColorButton.TooltipText = Localized(
            $"添加颜色 #{normalized.ToHtml(true).ToUpperInvariant()}",
            $"Add color #{normalized.ToHtml(true).ToUpperInvariant()}");
        _confirmColorButton.AddThemeFontSizeOverride("font_size", 19);
        _confirmColorButton.AddThemeColorOverride("font_color", foreground);
        _confirmColorButton.AddThemeColorOverride("font_hover_color", foreground);
        _confirmColorButton.AddThemeColorOverride("font_pressed_color", foreground);
        _confirmColorButton.AddThemeStyleboxOverride("normal", CreateColorPickerActionStyle(normalized, foreground, 3));
        _confirmColorButton.AddThemeStyleboxOverride("hover", CreateColorPickerActionStyle(hoverFill, foreground, 3));
        _confirmColorButton.AddThemeStyleboxOverride("pressed", CreateColorPickerActionStyle(pressedFill, foreground, 3));
        _confirmColorButton.AddThemeStyleboxOverride("focus", CreateColorPickerActionStyle(hoverFill, foreground, 3));
    }

    private void ConfirmCustomColor()
    {
        Color color = DrawingCommand.UnpackRgb(DrawingCommand.PackRgb(_colorPicker.Color));
        if (DrawingPaletteStore.TryRemember(_paletteOwner, color))
        {
            _customColors.Clear();
            _customColors.AddRange(DrawingPaletteStore.GetColors(_paletteOwner));
        }
        else
        {
            _customColors.Insert(0, color);
            if (_customColors.Count > CustomColorCapacity)
            {
                _customColors.RemoveAt(_customColors.Count - 1);
            }
        }
        RefreshCustomColorButtons();
        SelectColor(color);
        CloseColorPicker();
    }

    private static Button CreateDrawingToolButton(Texture2D icon, string text, string tooltip, ButtonGroup group)
    {
        Button button = new()
        {
            Icon = icon,
            Text = text,
            ExpandIcon = true,
            TooltipText = tooltip,
            ToggleMode = true,
            ButtonGroup = group,
            CustomMinimumSize = new Vector2(116f, 42f)
        };
        StyleBoxFlat selected = new()
        {
            BgColor = new Color("176B72"),
            BorderColor = new Color("75F0E6"),
            BorderWidthLeft = 3,
            BorderWidthTop = 3,
            BorderWidthRight = 3,
            BorderWidthBottom = 3,
            CornerRadiusTopLeft = 7,
            CornerRadiusTopRight = 7,
            CornerRadiusBottomLeft = 7,
            CornerRadiusBottomRight = 7
        };
        button.AddThemeStyleboxOverride("pressed", selected);
        button.AddThemeStyleboxOverride("hover_pressed", selected);
        button.AddThemeColorOverride("font_pressed_color", Colors.White);
        button.AddThemeColorOverride("font_hover_pressed_color", Colors.White);
        return button;
    }

    private void SelectDrawingTool(Button selected)
    {
        _brushToolButton.ButtonPressed = ReferenceEquals(selected, _brushToolButton);
        _fillToolButton.ButtonPressed = ReferenceEquals(selected, _fillToolButton);
    }

    private void ActivateBrushTool()
    {
        SelectDrawingTool(_brushToolButton);
        _canvas.SetBrushTool();
        ConfigureSizeControl(showStampSize: false);
    }

    private void ActivateFillTool()
    {
        SelectDrawingTool(_fillToolButton);
        _canvas.SetFillTool();
        ConfigureSizeControl(showStampSize: false);
    }

    private void ClearDrawingToolSelection()
    {
        _brushToolButton.ButtonPressed = false;
        _fillToolButton.ButtonPressed = false;
    }

    private void ConfigureSizeControl(bool showStampSize)
    {
        _showingStampSize = showStampSize;
        _syncingSizeSlider = true;
        if (showStampSize)
        {
            _sizeSlider.MinValue = DrawingCanvas.MinStampSize;
            _sizeSlider.MaxValue = DrawingCanvas.MaxStampSize;
            _sizeSlider.Value = _stampSize;
            _sizeSlider.TooltipText = Localized(
                "调整角色印花大小",
                "Adjust character stamp size");
            _sizeLabel.Text = Localized($"印花：{_stampSize} px", $"Stamp: {_stampSize} px");
        }
        else
        {
            _sizeSlider.MinValue = DrawingCanvas.MinBrushSize;
            _sizeSlider.MaxValue = DrawingCanvas.MaxBrushSize;
            _sizeSlider.Value = _brushSize;
            _sizeSlider.TooltipText = Localized(
                "调整画笔粗细",
                "Adjust brush size");
            _sizeLabel.Text = Localized($"粗细：{_brushSize} px", $"Size: {_brushSize} px");
        }
        _syncingSizeSlider = false;
    }

    private void AddStampButton(HBoxContainer tools, CharacterModel character, byte stampIndex)
    {
        string characterName = character.Title.GetFormattedText();
        Texture2D texture = character.IconTexture;
        _canvas.RegisterStamp(stampIndex, texture);
        Button button = new()
        {
            Icon = texture,
            ExpandIcon = true,
            TooltipText = characterName,
            CustomMinimumSize = new Vector2(46f, 42f)
        };
        button.Pressed += () =>
        {
            if (_canvas.SetStampTool(stampIndex))
            {
                ClearDrawingToolSelection();
                ConfigureSizeControl(showStampSize: true);
            }
            else
            {
                _status.Text = Localized(
                    "无法读取角色头像：" + characterName,
                    "Could not load character portrait: " + characterName);
            }
        };
        tools.AddChild(button);
    }

    internal static bool TryReceiveCommands(DrawingSyncMessage message, ulong senderId)
    {
        if (_active == null || !GodotObject.IsInstanceValid(_active) || _active._owner.NetId != message.OwnerId || _active._sessionId != message.SessionId)
        {
            return false;
        }

        _active.ReceiveCommands(message, senderId);
        return true;
    }

    internal static bool TryReceiveFinal(DrawingFinalMessage message)
    {
        if (_active == null || !GodotObject.IsInstanceValid(_active) || _active._owner.NetId != message.OwnerId || _active._sessionId != message.SessionId)
        {
            return false;
        }

        _active.ReceiveFinal(message);
        return true;
    }

    internal static bool TryReceiveUndoRequest(DrawingUndoRequestMessage message, ulong senderId)
    {
        if (_active == null || !GodotObject.IsInstanceValid(_active) || !_active._isChooser ||
            _active._owner.NetId != message.OwnerId || _active._sessionId != message.SessionId)
        {
            return false;
        }

        _active.ApplyAuthoritativeUndo(senderId);
        return true;
    }

    internal static bool TryReceiveRedoRequest(DrawingRedoRequestMessage message, ulong senderId)
    {
        if (_active == null || !GodotObject.IsInstanceValid(_active) || !_active._isChooser ||
            _active._owner.NetId != message.OwnerId || _active._sessionId != message.SessionId)
        {
            return false;
        }

        _active.ApplyAuthoritativeRedo(senderId);
        return true;
    }

    internal static bool TryReceiveCanvasState(DrawingCanvasStateMessage message)
    {
        if (_active == null || !GodotObject.IsInstanceValid(_active) || _active._owner.NetId != message.OwnerId ||
            _active._sessionId != message.SessionId)
        {
            return false;
        }

        _active.ReceiveCanvasState(message);
        return true;
    }

    internal static bool TryReceiveTimer(DrawingTimerSyncMessage message)
    {
        if (_active == null || !GodotObject.IsInstanceValid(_active) || _active._owner.NetId != message.OwnerId ||
            _active._sessionId != message.SessionId)
        {
            return false;
        }

        _active.ReceiveTimer(message);
        return true;
    }

    internal static bool TryReceiveBlankSettings(DrawingBlankSettingsMessage message)
    {
        if (_active == null || !GodotObject.IsInstanceValid(_active) ||
            _active._owner.NetId != message.OwnerId ||
            _active._sessionId != message.SessionId)
        {
            return false;
        }

        _active.ReceiveBlankSettings(message);
        return true;
    }

    internal void ReceiveCommands(DrawingSyncMessage message, ulong senderId)
    {
        if (_finishing || senderId == LocalContext.NetId || message.Epoch != _historyEpoch)
        {
            return;
        }

        foreach (DrawingCommand command in message.Commands)
        {
            if (DrawingNetSync.IsMultiplayer)
            {
                if (IsOperationSettled(senderId, command.OperationId))
                {
                    continue;
                }

                UpdateCanvasModeLock(command);
                TrackPendingMultiplayerCommand(senderId, command);
                _canvas.ApplyRemote(command);
                if (_isChooser && command.CompletesOperation)
                {
                    CommitAuthoritativeOperation(new DrawingOperationKey(senderId, command.OperationId));
                }
                continue;
            }

            if (_isChooser)
            {
                RecordHistoryCommand(senderId, command);
            }
            UpdateCanvasModeLock(command);
            _canvas.ApplyRemote(command);
        }
    }

    internal void ReceiveCanvasState(DrawingCanvasStateMessage message)
    {
        if (_finishing ||
            message.Epoch < _historyEpoch ||
            message.Epoch == _historyEpoch && message.StateSequence <= _canvasStateSequence)
        {
            return;
        }

        bool resetPending = message.ResetPendingOperations || message.Epoch > _historyEpoch;
        if (message.CanvasMode != _canvasMode)
        {
            _canvasMode = message.CanvasMode;
            _canvas.SetCanvasMode(_canvasMode);
            ResetDrawingHistory();
            _canvasModeLocked = false;
            RefreshCanvasModeButton();
            resetPending = true;
        }
        if (!_canvas.ImportPng(message.PngBytes, cancelActiveOperation: resetPending))
        {
            Entry.Logger.Warn("[DrawAndGuessMod] Received an invalid authoritative canvas state.");
            return;
        }
        if (resetPending)
        {
            _canvasModeLocked = !_canvas.IsBlank();
            RefreshCanvasModeButton();
        }

        _historyEpoch = message.Epoch;
        _canvasStateSequence = message.StateSequence;
        ApplyOperationWatermarks(message.Watermarks);
        RemoveSettledLocalCommandQueue();
        if (resetPending)
        {
            _pendingCommands.Clear();
            _pendingMultiplayerOperations.Clear();
            _status.Text = Localized(
                "画布已同步。",
                "The canvas has been synchronized.");
            return;
        }

        RemoveSettledPendingOperations();
        ReapplyPendingMultiplayerPreviews();
    }

    internal void ReceiveTimer(DrawingTimerSyncMessage message)
    {
        if (_finishing || _isTimerAuthority)
        {
            return;
        }

        double synchronizedRemaining = Math.Max(0d, message.RemainingMilliseconds / 1000d);
        if (!_receivedAuthoritativeTimer)
        {
            _receivedAuthoritativeTimer = true;
            _remainingSeconds = synchronizedRemaining;
            EnsureTimerBar(synchronizedRemaining);
        }
        else if (_remainingSeconds.HasValue)
        {
            _remainingSeconds = Math.Min(_remainingSeconds.Value, synchronizedRemaining);
        }

        UpdateTimerBar();
        if (synchronizedRemaining <= 0d && _isChooser)
        {
            OnGuessPressed();
        }
    }

    internal void ReceiveBlankSettings(DrawingBlankSettingsMessage message)
    {
        if (_finishing || !_isRegularBlank || _isTimerAuthority)
        {
            return;
        }

        _excludePreviouslySelectedBlankCards = message.ExcludePreviouslySelectedCards;
        _receivedAuthoritativeBlankSettings = true;
        if (_isChooser)
        {
            _guessButton.Disabled = false;
        }
    }

    internal void ReceiveFinal(DrawingFinalMessage message)
    {
        if (_finishing)
        {
            return;
        }

        _finishing = true;
        RefreshCanvasModeButton();
        if (message.Cancelled)
        {
            Complete(null);
            return;
        }

        List<CardModel> candidates = new(message.CardIds.Count);
        for (int index = 0; index < message.CardIds.Count; index++)
        {
            string cardId = message.CardIds[index];
            CardModel? candidate = ModelDb.AllCards.FirstOrDefault(card => string.Equals(card.Id.Entry, cardId, StringComparison.Ordinal));
            if (candidate == null)
            {
                continue;
            }

            candidates.Add(candidate);
        }
        if (message.CanvasMode != _canvasMode)
        {
            _canvasMode = message.CanvasMode;
            _canvas.SetCanvasMode(_canvasMode);
            RefreshCanvasModeButton();
        }
        if (candidates.Count == 0 || !_canvas.ImportPng(message.PngBytes))
        {
            Entry.Logger.Warn("[DrawAndGuessMod] Received an invalid collaborative drawing final.");
            Complete(null);
            return;
        }

        CardGuess guess = new(candidates[0], 0, 0d, candidates);
        Complete(new DrawingResult(message.PngBytes, guess, message.SkipAddingToDeck));
    }

    private void SendTimerSync(bool force)
    {
        if (!DrawingNetSync.IsMultiplayer || !_isTimerAuthority || !_remainingSeconds.HasValue)
        {
            return;
        }

        ulong now = Time.GetTicksMsec();
        if (!force && now < _lastTimerSyncMsec + TimerSyncIntervalMsec)
        {
            return;
        }

        _lastTimerSyncMsec = now;
        DrawingNetSync.SendTimer(_owner.NetId, _sessionId, _remainingSeconds.Value);
    }

    private void SendBlankSettings()
    {
        if (!DrawingNetSync.IsMultiplayer || !_isTimerAuthority || !_isRegularBlank)
        {
            return;
        }

        DrawingNetSync.SendBlankSettings(
            _owner.NetId,
            _sessionId,
            _excludePreviouslySelectedBlankCards);
    }

    private void OnLocalCommand(DrawingCommand command)
    {
        UpdateCanvasModeLock(command);
        if (_privateDrawing && command.CompletesOperation)
        {
            UpdateRelicGuess();
        }

        if (UsesCollaborativeNetworking)
        {
            ulong senderId = RunManager.Instance.NetService.NetId;
            TrackPendingMultiplayerCommand(senderId, command);
            if (_isChooser && command.CompletesOperation)
            {
                CommitAuthoritativeOperation(new DrawingOperationKey(senderId, command.OperationId));
            }
            if (IsOperationSettled(senderId, command.OperationId))
            {
                _pendingCommands.RemoveAll(candidate => candidate.OperationId == command.OperationId);
                return;
            }
        }
        else if (_isChooser)
        {
            RecordHistoryCommand(_owner.NetId, command);
        }

        if (!UsesCollaborativeNetworking || _finishing)
        {
            return;
        }

        _pendingCommands.Add(command);
        if (_pendingCommands.Count >= 64)
        {
            FlushCommands();
        }
    }

    private void FlushCommands()
    {
        if (_pendingCommands.Count == 0)
        {
            return;
        }

        if (_privateDrawing)
        {
            _pendingCommands.Clear();
            return;
        }

        DrawingNetSync.SendCommands(_owner.NetId, _sessionId, _historyEpoch, _pendingCommands);
        _pendingCommands.Clear();
        _lastCommandFlushMsec = Time.GetTicksMsec();
    }

    private void RequestUndo()
    {
        if (_finishing)
        {
            return;
        }

        FlushCommands();
        if (UsesCollaborativeNetworking)
        {
            if (_isChooser)
            {
                ApplyAuthoritativeUndo(RunManager.Instance.NetService.NetId);
                return;
            }

            DrawingNetSync.SendUndoRequest(_owner.NetId, _sessionId);
            _status.Text = Localized(
                "已请求撤回你自己的最近一次操作。",
                "Requested an undo of your most recent action.");
            return;
        }

        if (_isChooser)
        {
            ApplyAuthoritativeUndo(_owner.NetId);
            return;
        }

        DrawingNetSync.SendUndoRequest(_owner.NetId, _sessionId);
        _status.Text = Localized(
            "已请求出牌者撤回最近一次操作。",
            "Asked the player who played the card to undo the most recent action.");
    }

    private void RequestRedo()
    {
        if (_finishing)
        {
            return;
        }

        FlushCommands();
        if (UsesCollaborativeNetworking)
        {
            if (_isChooser)
            {
                ApplyAuthoritativeRedo(RunManager.Instance.NetService.NetId);
                return;
            }

            DrawingNetSync.SendRedoRequest(_owner.NetId, _sessionId);
            _status.Text = Localized(
                "已请求重做你自己的最近一次撤回操作。",
                "Requested a redo of your most recently undone action.");
            return;
        }

        if (_isChooser)
        {
            ApplyAuthoritativeRedo(_owner.NetId);
        }
    }

    private void ApplyAuthoritativeUndo(ulong requesterId)
    {
        if (_finishing || !_isChooser)
        {
            return;
        }

        FlushCommands();
        if (UsesCollaborativeNetworking)
        {
            ApplyAuthoritativeMultiplayerUndo(requesterId);
            return;
        }

        LinkedListNode<DrawingOperationKey>? last = _undoableOperations.Last;
        if (last == null)
        {
            _status.Text = Localized(
                "没有可以撤回的操作。",
                "There are no actions to undo.");
            return;
        }

        DrawingOperationKey operation = last.Value;
        _undoableOperations.RemoveLast();
        _undoneOperations.Add(operation);
        _redoableOperations.Push(operation);
        foreach (DrawingOperationKey incompleteOperation in _historyCommands
                     .Select(entry => entry.Operation)
                     .Where(candidate => !_completedOperations.Contains(candidate))
                     .Distinct())
        {
            _undoneOperations.Add(incompleteOperation);
        }
        _canvas.RebuildFromCommands(_historyCommands
            .Where(entry => !_undoneOperations.Contains(entry.Operation))
            .Select(entry => entry.Command));
        _canvasModeLocked = !_canvas.IsBlank();
        RefreshCanvasModeButton();
        _historyEpoch++;
        _pendingCommands.Clear();
        _status.Text = Localized(
            $"已撤回最近一次操作（剩余 {_undoableOperations.Count} 步可撤回）。",
            $"Undid the most recent action ({_undoableOperations.Count} undoable actions remain).");
        Entry.Logger.Debug($"[DrawAndGuessMod] Undo requested by {requesterId}: sender={operation.SenderId}, operation={operation.OperationId}, epoch={_historyEpoch}.");
        if (UsesCollaborativeNetworking)
        {
            DrawingNetSync.SendCanvasState(new DrawingCanvasStateMessage
            {
                OwnerId = _owner.NetId,
                SessionId = _sessionId,
                Epoch = _historyEpoch,
                StateSequence = ++_canvasStateSequence,
                CanvasMode = _canvasMode,
                ResetPendingOperations = true,
                PngBytes = _canvas.ExportPng()
            });
        }
        UpdateRelicGuess();
    }

    private void ApplyAuthoritativeRedo(ulong requesterId)
    {
        if (_finishing || !_isChooser)
        {
            return;
        }

        FlushCommands();
        if (UsesCollaborativeNetworking)
        {
            ApplyAuthoritativeMultiplayerRedo(requesterId);
            return;
        }

        if (!_redoableOperations.TryPop(out DrawingOperationKey operation))
        {
            _status.Text = Localized(
                "没有可以重做的操作。",
                "There are no actions to redo.");
            return;
        }

        _undoneOperations.Remove(operation);
        _undoableOperations.AddLast(operation);
        if (_undoableOperations.Count > MaxUndoSteps)
        {
            _undoableOperations.RemoveFirst();
        }
        _canvas.RebuildFromCommands(_historyCommands
            .Where(entry => !_undoneOperations.Contains(entry.Operation))
            .Select(entry => entry.Command));
        _canvasModeLocked = !_canvas.IsBlank();
        RefreshCanvasModeButton();
        _historyEpoch++;
        if (_historyEpoch == 0u)
        {
            _historyEpoch = 1u;
        }
        _pendingCommands.Clear();
        _status.Text = Localized(
            $"已重做最近一次撤回操作（剩余 {_redoableOperations.Count} 步可重做）。",
            $"Redid the most recently undone action ({_redoableOperations.Count} redoable actions remain).");
        Entry.Logger.Debug(
            $"[DrawAndGuessMod] Redo requested by {requesterId}: sender={operation.SenderId}, " +
            $"operation={operation.OperationId}, epoch={_historyEpoch}.");
        if (UsesCollaborativeNetworking)
        {
            DrawingNetSync.SendCanvasState(new DrawingCanvasStateMessage
            {
                OwnerId = _owner.NetId,
                SessionId = _sessionId,
                Epoch = _historyEpoch,
                StateSequence = ++_canvasStateSequence,
                CanvasMode = _canvasMode,
                ResetPendingOperations = true,
                PngBytes = _canvas.ExportPng()
            });
        }
        UpdateRelicGuess();
    }

    private void TrackPendingMultiplayerCommand(ulong senderId, DrawingCommand command)
    {
        if (command.OperationId == 0u || IsOperationSettled(senderId, command.OperationId))
        {
            return;
        }

        DrawingOperationKey operation = new(senderId, command.OperationId);
        if (!_pendingMultiplayerOperations.TryGetValue(operation, out List<DrawingCommand>? commands))
        {
            commands = new List<DrawingCommand>();
            _pendingMultiplayerOperations[operation] = commands;
        }
        commands.Add(command);
    }

    private void CommitAuthoritativeOperation(DrawingOperationKey operation)
    {
        if (!_isChooser ||
            !_pendingMultiplayerOperations.Remove(operation, out List<DrawingCommand>? commands) ||
            commands.Count == 0 ||
            !commands[^1].CompletesOperation)
        {
            return;
        }

        RebuildAuthoritativeCanvas();
        Image before = _canvas.Snapshot();
        _canvas.ApplyCommands(commands);
        DrawingPixelPatch patch = DrawingPixelPatch.Between(before, _canvas.Snapshot());
        CollaborativeDrawingHistory<DrawingPixelPatch>.Entry committed =
            _multiplayerHistory.Commit(operation, patch);
        AdvanceOperationWatermark(operation);

        byte[] canonicalPng = _canvas.ExportPng();
        _canvasStateSequence++;
        ReapplyPendingMultiplayerPreviews();
        SendAuthoritativeCanvasState(canonicalPng, resetPendingOperations: false);
        Entry.Logger.Debug(
            $"[DrawAndGuessMod] Committed drawing operation: sender={operation.SenderId}, " +
            $"operation={operation.OperationId}, sequence={committed.Sequence}, epoch={_historyEpoch}.");
    }

    private void ApplyAuthoritativeMultiplayerUndo(ulong requesterId)
    {
        if (!_multiplayerHistory.TryUndoLatest(
                requesterId,
                out CollaborativeDrawingHistory<DrawingPixelPatch>.Entry operation))
        {
            _status.Text = Localized(
                "你没有可以撤回的操作。",
                "You have no actions to undo.");
            return;
        }

        _canvas.CancelActiveOperation();
        _pendingCommands.Clear();
        _pendingMultiplayerOperations.Clear();
        RebuildAuthoritativeCanvas();
        _canvasModeLocked = !_canvas.IsBlank();
        RefreshCanvasModeButton();
        _historyEpoch++;
        if (_historyEpoch == 0u)
        {
            _historyEpoch = 1u;
        }
        _canvasStateSequence++;
        _status.Text = Localized(
            "已撤回你自己的最近一次操作。",
            "Undid your most recent action.");
        Entry.Logger.Debug(
            $"[DrawAndGuessMod] Authoritative multiplayer undo: requester={requesterId}, " +
            $"operation={operation.Operation.OperationId}, sequence={operation.Sequence}, epoch={_historyEpoch}.");
        SendAuthoritativeCanvasState(_canvas.ExportPng(), resetPendingOperations: true);
    }

    private void ApplyAuthoritativeMultiplayerRedo(ulong requesterId)
    {
        if (!_multiplayerHistory.TryRedoLatest(
                requesterId,
                out CollaborativeDrawingHistory<DrawingPixelPatch>.Entry operation))
        {
            _status.Text = Localized(
                "你没有可以重做的操作。",
                "You have no actions to redo.");
            return;
        }

        _canvas.CancelActiveOperation();
        _pendingCommands.Clear();
        _pendingMultiplayerOperations.Clear();
        RebuildAuthoritativeCanvas();
        _canvasModeLocked = !_canvas.IsBlank();
        RefreshCanvasModeButton();
        _historyEpoch++;
        if (_historyEpoch == 0u)
        {
            _historyEpoch = 1u;
        }
        _canvasStateSequence++;
        _status.Text = Localized(
            "已重做你自己的最近一次撤回操作。",
            "Redid your most recently undone action.");
        Entry.Logger.Debug(
            $"[DrawAndGuessMod] Authoritative multiplayer redo: requester={requesterId}, " +
            $"operation={operation.Operation.OperationId}, sequence={operation.Sequence}, epoch={_historyEpoch}.");
        SendAuthoritativeCanvasState(_canvas.ExportPng(), resetPendingOperations: true);
    }

    private void RebuildAuthoritativeCanvas()
    {
        _canvas.RebuildFromPixelPatches(
            _multiplayerHistory.GetActivePatches());
    }

    private void ReapplyPendingMultiplayerPreviews()
    {
        if (_pendingMultiplayerOperations.Count == 0)
        {
            return;
        }

        _canvas.ApplyCommands(_pendingMultiplayerOperations.Values.SelectMany(commands => commands));
    }

    private void SendAuthoritativeCanvasState(byte[] pngBytes, bool resetPendingOperations)
    {
        DrawingNetSync.SendCanvasState(new DrawingCanvasStateMessage
        {
            OwnerId = _owner.NetId,
            SessionId = _sessionId,
            Epoch = _historyEpoch,
            StateSequence = _canvasStateSequence,
            CanvasMode = _canvasMode,
            ResetPendingOperations = resetPendingOperations,
            Watermarks = _settledOperationWatermarks
                .Select(pair => new DrawingOperationWatermark
                {
                    SenderId = pair.Key,
                    OperationId = pair.Value
                })
                .ToList(),
            PngBytes = pngBytes
        });
    }

    private void ApplyOperationWatermarks(IEnumerable<DrawingOperationWatermark> watermarks)
    {
        foreach (DrawingOperationWatermark watermark in watermarks)
        {
            if (!_settledOperationWatermarks.TryGetValue(watermark.SenderId, out uint current) ||
                watermark.OperationId > current)
            {
                _settledOperationWatermarks[watermark.SenderId] = watermark.OperationId;
            }
        }
    }

    private void AdvanceOperationWatermark(DrawingOperationKey operation)
    {
        if (!_settledOperationWatermarks.TryGetValue(operation.SenderId, out uint current) ||
            operation.OperationId > current)
        {
            _settledOperationWatermarks[operation.SenderId] = operation.OperationId;
        }
    }

    private bool IsOperationSettled(ulong senderId, uint operationId)
    {
        return operationId != 0u &&
               _settledOperationWatermarks.TryGetValue(senderId, out uint watermark) &&
               operationId <= watermark;
    }

    private void RemoveSettledPendingOperations()
    {
        foreach (DrawingOperationKey operation in _pendingMultiplayerOperations.Keys
                     .Where(operation => IsOperationSettled(operation.SenderId, operation.OperationId))
                     .ToList())
        {
            _pendingMultiplayerOperations.Remove(operation);
        }
    }

    private void RemoveSettledLocalCommandQueue()
    {
        ulong localSenderId = RunManager.Instance.NetService.NetId;
        _pendingCommands.RemoveAll(command =>
            IsOperationSettled(localSenderId, command.OperationId));
    }

    private void UpdateCanvasModeLock(DrawingCommand command)
    {
        bool locked = command.Kind switch
        {
            DrawingCommandKind.Clear => false,
            DrawingCommandKind.Line or DrawingCommandKind.Fill or DrawingCommandKind.Stamp => true,
            _ => _canvasModeLocked
        };
        if (locked == _canvasModeLocked)
        {
            return;
        }

        _canvasModeLocked = locked;
        RefreshCanvasModeButton();
    }

    private void ResetDrawingHistory()
    {
        _canvas.CancelActiveOperation();
        _pendingCommands.Clear();
        _historyCommands.Clear();
        _undoableOperations.Clear();
        _redoableOperations.Clear();
        _completedOperations.Clear();
        _undoneOperations.Clear();
        _pendingMultiplayerOperations.Clear();
        _multiplayerHistory.Reset();
        _settledOperationWatermarks.Clear();
        _canvasModeLocked = false;
    }

    private void AdvanceHistoryEpoch()
    {
        _historyEpoch++;
        if (_historyEpoch == 0u)
        {
            _historyEpoch = 1u;
        }
    }

    private void RecordHistoryCommand(ulong senderId, DrawingCommand command)
    {
        if (command.OperationId == 0u)
        {
            return;
        }

        DrawingOperationKey operation = new(senderId, command.OperationId);
        _historyCommands.Add(new RecordedDrawingCommand(operation, command));
        if (!command.CompletesOperation || !_completedOperations.Add(operation))
        {
            return;
        }

        _redoableOperations.Clear();
        _undoableOperations.AddLast(operation);
        if (_undoableOperations.Count > MaxUndoSteps)
        {
            _undoableOperations.RemoveFirst();
        }
    }

    private sealed record RecordedDrawingCommand(DrawingOperationKey Operation, DrawingCommand Command);

    private void Complete(DrawingResult? result)
    {
        if (_completion.TrySetResult(result))
        {
            QueueFree();
        }
    }

    private void CompleteRelic(RelicDrawingResult? result)
    {
        if (_relicCompletion.TrySetResult(result))
        {
            QueueFree();
        }
    }

    private bool UsesCollaborativeNetworking => DrawingNetSync.IsMultiplayer && !_privateDrawing;

    private void BuildRelicTitleEditor(Container column)
    {
        HBoxContainer titleRow = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center
        };
        titleRow.AddThemeConstantOverride("separation", 12);
        column.AddChild(titleRow);

        _relicWorkTitleLabel = new Label
        {
            Text = Localized("无题作品", "Untitled Work"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _relicWorkTitleLabel.AddThemeFontSizeOverride("font_size", 30);
        titleRow.AddChild(_relicWorkTitleLabel);

        _relicWorkTitleInput = new LineEdit
        {
            Text = _relicWorkTitleLabel.Text,
            MaxLength = 32,
            CustomMinimumSize = new Vector2(360f, 46f),
            Visible = false,
            TooltipText = Localized(
                "拍卖时会显示这个名字；你可以用它伪装遗物。",
                "This title is shown at auction; you may use it to disguise the relic.")
        };
        _relicWorkTitleInput.AddThemeFontSizeOverride("font_size", 26);
        _relicWorkTitleInput.TextChanged += _ => UpdateRelicGuessStatus();
        titleRow.AddChild(_relicWorkTitleInput);

        _relicWorkTitleEditButton = CreateActionButton(
            Localized("修改", "Edit"),
            new Color("253D58"),
            new Color("79BCE8"));
        _relicWorkTitleEditButton.TooltipText = Localized(
            "修改拍卖时显示的作品名称",
            "Edit the work title shown during the auction");
        _relicWorkTitleEditButton.Pressed += ToggleRelicWorkTitleEditing;
        titleRow.AddChild(_relicWorkTitleEditButton);
    }

    private void ToggleRelicWorkTitleEditing()
    {
        if (_relicWorkTitleInput == null ||
            _relicWorkTitleLabel == null ||
            _relicWorkTitleEditButton == null)
        {
            return;
        }

        if (!_editingRelicWorkTitle)
        {
            if (_gFillHeld)
            {
                _gFillHeld = false;
                ActivateBrushTool();
            }
            _editingRelicWorkTitle = true;
            _relicWorkTitleInput.Text = _relicWorkTitleLabel.Text;
            _relicWorkTitleLabel.Visible = false;
            _relicWorkTitleInput.Visible = true;
            _relicWorkTitleEditButton.Text = Localized("确认", "Confirm");
            _relicWorkTitleInput.GrabFocus();
            _relicWorkTitleInput.SelectAll();
            UpdateRelicGuessStatus();
            return;
        }

        string editedTitle = _relicWorkTitleInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(editedTitle))
        {
            return;
        }

        _relicWorkTitleLabel.Text = editedTitle;
        _relicWorkTitleInput.ReleaseFocus();
        _relicWorkTitleInput.Visible = false;
        _relicWorkTitleLabel.Visible = true;
        _relicWorkTitleEditButton.Text = Localized("修改", "Edit");
        _editingRelicWorkTitle = false;
        UpdateRelicGuessStatus();
    }

    private bool IsEditingRelicWorkTitle()
    {
        return _editingRelicWorkTitle ||
               (_relicWorkTitleInput?.HasFocus() ?? false);
    }

    private string GetRelicWorkTitle()
    {
        string? title = _relicWorkTitleLabel?.Text;
        return string.IsNullOrWhiteSpace(title)
            ? Localized("无题作品", "Untitled Work")
            : title.Trim();
    }

    private void UpdateRelicGuess()
    {
        if (_relicTarget == null || _finishing)
        {
            return;
        }

        _currentRelicGuess = _canvas.IsBlank()
            ? null
            : RelicArtClassifier.GuessTopOne(_canvas.Snapshot());
        UpdateRelicGuessStatus();
    }

    private void UpdateRelicGuessStatus()
    {
        if (_relicTarget == null)
        {
            return;
        }

        string guessName = _currentRelicGuess?.Relic.Title.GetFormattedText() ??
                           Localized("尚未识别", "No guess");
        bool matches = _currentRelicGuess?.Relic.Id == _relicTarget.Id;
        if (_relicGuessImage != null)
        {
            _relicGuessImage.Texture = _currentRelicGuess?.Relic.BigIcon;
        }
        _status.Text = Localized(
            $"当前识别：{guessName}" +
            (matches ? "\n识别正确，可以提交作品。" : "\n必须与题目完全一致才能提交。"),
            $"Current guess: {guessName}" +
            (matches ? "\nExact match. You may submit the work." : "\nThe guess must exactly match the target before submission."));
        _guessButton.Disabled =
            !matches ||
            _editingRelicWorkTitle ||
            string.IsNullOrWhiteSpace(_relicWorkTitleLabel?.Text);
    }

    private void BuildRelicReferencePanel(Container canvasRow)
    {
        if (_relicTarget == null)
        {
            return;
        }

        VBoxContainer references = new()
        {
            CustomMinimumSize = new Vector2(230f, 0f),
            Alignment = BoxContainer.AlignmentMode.Center
        };
        references.AddThemeConstantOverride("separation", 6);
        canvasRow.AddChild(references);

        Label guessLabel = new()
        {
            Text = Localized("瓦库的猜测", "VAKUU's Guess"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        references.AddChild(guessLabel);
        _relicGuessImage = new TextureRect
        {
            CustomMinimumSize = new Vector2(190f, 190f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
        };
        references.AddChild(_relicGuessImage);

        Label targetLabel = new()
        {
            Text = Localized(
                $"题目参考：{_relicTarget.Title.GetFormattedText()}",
                $"Target Reference: {_relicTarget.Title.GetFormattedText()}"),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        references.AddChild(targetLabel);
        _relicTargetImage = new TextureRect
        {
            Texture = _relicTarget.BigIcon,
            CustomMinimumSize = new Vector2(190f, 190f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
        };
        references.AddChild(_relicTargetImage);

    }

    private static string Localized(string simplifiedChinese, string english)
    {
        return ModText.Get(simplifiedChinese, english);
    }

    private sealed partial class CanvasModeIconControl : Control
    {
        private DrawingCanvasMode _mode;
        private bool _muted;

        public void SetMode(DrawingCanvasMode mode, bool muted)
        {
            _mode = mode;
            _muted = muted;
            QueueRedraw();
        }

        public override void _Draw()
        {
            Color line = _muted ? new Color("59636D") : new Color("AEB8C2");
            Color accent = _muted ? new Color("46505B") : new Color("75F0E6");
            Rect2 standardCard = new(new Vector2(6f, 21f), new Vector2(29f, 22f));
            Rect2 ancientCard = new(new Vector2(41f, 14f), new Vector2(17f, 36f));
            DrawCardOutline(
                standardCard,
                _mode == DrawingCanvasMode.Standard ? accent : line,
                _mode == DrawingCanvasMode.Standard);
            DrawCardOutline(
                ancientCard,
                _mode == DrawingCanvasMode.Ancient ? accent : line,
                _mode == DrawingCanvasMode.Ancient);
            DrawLine(new Vector2(34f, 17f), new Vector2(42f, 11f), accent, 1.8f, true);
            DrawLine(new Vector2(42f, 11f), new Vector2(39f, 17f), accent, 1.8f, true);
            DrawLine(new Vector2(42f, 53f), new Vector2(34f, 47f), accent, 1.8f, true);
            DrawLine(new Vector2(34f, 47f), new Vector2(37f, 53f), accent, 1.8f, true);
        }

        private void DrawCardOutline(Rect2 rect, Color color, bool selected)
        {
            if (selected)
            {
                DrawRect(rect, new Color(color, 0.18f), true);
            }
            DrawRect(rect, color, false, selected ? 2.8f : 1.8f);
        }
    }

    private sealed partial class EyeIconControl : Control
    {
        private bool _active;

        public void SetActive(bool active)
        {
            _active = active;
            QueueRedraw();
        }

        public override void _Draw()
        {
            Color line = _active ? new Color("75F0E6") : new Color("D8E0E8");
            Color fill = _active ? new Color(0.46f, 0.94f, 0.90f, 0.28f) : new Color(0.50f, 0.58f, 0.66f, 0.18f);
            Vector2 center = Size * 0.5f;
            float radiusX = Math.Max(16f, Size.X * 0.30f);
            float radiusY = Math.Max(7f, Size.Y * 0.15f);
            Vector2 left = center + new Vector2(-radiusX, 0f);
            Vector2 right = center + new Vector2(radiusX, 0f);
            Vector2 top = center + new Vector2(0f, -radiusY);
            Vector2 bottom = center + new Vector2(0f, radiusY);

            DrawCircle(center, radiusY * 1.55f, fill);
            DrawLine(left, top, line, 2.4f, true);
            DrawLine(top, right, line, 2.4f, true);
            DrawLine(right, bottom, line, 2.4f, true);
            DrawLine(bottom, left, line, 2.4f, true);
            DrawCircle(center, radiusY * 0.62f, line);
            DrawCircle(center + new Vector2(radiusY * 0.22f, -radiusY * 0.22f), Math.Max(1.5f, radiusY * 0.18f), Colors.White);
        }
    }
}
