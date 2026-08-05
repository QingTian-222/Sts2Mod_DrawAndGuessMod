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
    // Sender id used to key locally-recorded brush operations in the history
    // artwork editor. The editor is single-player (no net messages carry these),
    // so any stable non-zero value works and cannot collide with real player ids.
    private const ulong HistoryEditSenderId = 0xF000_0000_0000_0001uL;
    private const string PenNibIconPath =
        "res://images/atlases/relic_atlas.sprites/pen_nib.tres";
    private const string InkBottleIconPath =
        "res://images/atlases/relic_atlas.sprites/ink_bottle.tres";
    private static DrawingScreen? _active;
    private readonly TaskCompletionSource<DrawingResult?> _completion = new();
    private readonly TaskCompletionSource<RelicDrawingResult?> _relicCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<byte[]?> _historyEditCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    private Button _cancelButton = null!;
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
    private bool _historyEdit;
    private bool _historyEditFinalizing;
    private byte[]? _initialPng;
    private Action<byte[]?>? _historyEditClosed;
    private CanvasLayer? _historyEditLayer;
    private RelicModel? _relicTarget;
    private RelicArtAssessment? _currentRelicAssessment;
    private TextureRect? _relicTargetImage;
    private ProgressBar? _relicMatchBar;
    private StyleBoxFlat? _relicMatchFillStyle;
    private double _relicMatchTargetSimilarity;
    private double _relicMatchDisplayedSimilarity;
    private Label? _relicWorkTitleLabel;
    private LineEdit? _relicWorkTitleInput;
    private Control? _relicWorkTitleOverlay;
    private Control? _relicWaitingOverlay;
    private bool _editingRelicWorkTitle;
    private bool _relicDrawingConfirmed;
    private LineEdit? _tracingSearch;
    private VBoxContainer? _tracingResults;
    private ScrollContainer? _tracingResultsScroll;
    private TextureRect? _tracingReference;
    private Image? _tracingReferenceImage;
    private ImageTexture? _tracingReferenceTexture;
    private Control? _tracingPanel;
    private IReadOnlyList<CardModel> _searchableCards = [];
    private bool _tracingSearchPreparing;
    private bool _tracingSearchPrepared;

    public static Task<DrawingResult?> ShowAsync(Player owner, uint sessionId, DrawingScreenOptions? options = null, double? defaultTimeLimitSeconds = null, bool isRegularBlank = false)
    {
        if (_active != null && GodotObject.IsInstanceValid(_active))
        {
            if (!_active._historyEdit &&
                _active._owner.NetId == owner.NetId &&
                _active._sessionId == sessionId)
            {
                return _active._completion.Task;
            }

            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Rejected drawing session {owner.NetId}/{sessionId} because " +
                (_active._historyEdit
                    ? "a history artwork editor is still active."
                    : $"session {_active._owner.NetId}/{_active._sessionId} is still active."));
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
                                                       DrawingRunRules.GetExcludePreviouslySelectedCards(owner.RunState),
            _receivedAuthoritativeBlankSettings = hasAuthoritativeBlankSettings,
            _options = options,
            _canvasMode = options?.InitialCanvasMode ?? DrawingCanvasMode.Standard,
            _rightColor = options?.InitialCanvasMode == DrawingCanvasMode.Relic
                ? Colors.Transparent
                : DrawingCanvas.PaperColor,
            _remainingSeconds = effectiveTimeLimit,
            _timerDurationSeconds = effectiveTimeLimit
        };
        screen._customColors.AddRange(
            DrawingPaletteStore.GetColors(paletteOwner)
                .Select(color => new Color(color.R, color.G, color.B, 1f)));
        DrawingNetSync.BeginSession(owner.NetId, sessionId);
        _active = screen;
        tree.Root.AddChild(screen);
        return screen._completion.Task;
    }

    public static Task<byte[]?> ShowHistoryEditAsync(
        byte[] pngBytes,
        string artworkName,
        Action<byte[]?>? onClosed = null)
    {
        if (pngBytes.Length == 0 ||
            _active != null && GodotObject.IsInstanceValid(_active) ||
            Engine.GetMainLoop() is not SceneTree tree)
        {
            return Task.FromResult<byte[]?>(null);
        }

        DrawingCanvasMode canvasMode = DetectCanvasMode(pngBytes);
        DrawingScreen screen = new()
        {
            Name = "DrawAndGuessMod_HistoryArtworkEditor",
            _isChooser = true,
            _isTimerAuthority = true,
            _receivedAuthoritativeBlankSettings = true,
            _privateDrawing = true,
            _historyEdit = true,
            _initialPng = pngBytes,
            _historyEditClosed = onClosed,
            _canvasMode = canvasMode,
            _rightColor = canvasMode == DrawingCanvasMode.Relic
                ? Colors.Transparent
                : DrawingCanvas.PaperColor,
            _options = new DrawingScreenOptions(
                Localized($"编辑画作：{artworkName}", $"Edit artwork: {artworkName}"),
                Localized("完成后会覆盖保存这幅 PNG 画作。", "Confirm to overwrite this PNG artwork."),
                AllowCanvasModeSwitch: false)
        };
        _active = screen;
        // Host the editor on a dedicated high canvas layer so it always renders
        // above the still-open RitsuLib settings submenu. The layer is freed with
        // the editor, so closing the editor reveals the settings page underneath
        // and no submenu pop/push round-trip is required.
        CanvasLayer layer = new()
        {
            Name = "DrawAndGuessMod_HistoryArtworkEditorLayer",
            Layer = 100
        };
        screen._historyEditLayer = layer;
        tree.Root.AddChild(layer);
        layer.AddChild(screen);
        screen.MoveToFront();
        return screen._historyEditCompletion.Task;
    }

    public static Task<RelicDrawingResult?> ShowRelicAsync(
        Player owner,
        uint sessionId,
        RelicModel target,
        DrawingScreenOptions options)
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
        screen._customColors.AddRange(
            DrawingPaletteStore.GetColors(paletteOwner)
                .Select(color => new Color(color.R, color.G, color.B, 1f)));
        _active = screen;
        tree.Root.AddChild(screen);
        return screen._relicCompletion.Task;
    }

    public static void CloseCompletedRelicScreen()
    {
        if (_active == null ||
            !GodotObject.IsInstanceValid(_active) ||
            _active._relicTarget == null ||
            !_active._relicCompletion.Task.IsCompleted)
        {
            return;
        }

        _active.QueueFree();
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 4000;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        BuildUi();
        if (_historyEdit)
        {
            // The editor is shown over the still-open RitsuLib settings page, so
            // take focus and swallow Escape. Otherwise keyboard input would leak
            // into the settings controls underneath and Escape would pop the
            // settings submenu while the editor is still on screen.
            FocusMode = FocusModeEnum.All;
            GrabFocus();
        }
        if (_historyEdit && _initialPng is { Length: > 0 })
        {
            if (_canvas.ImportPng(_initialPng, preserveDimensions: true))
            {
                // The imported artwork becomes the undo base so "undo to the
                // start" returns to the original drawing instead of blank.
                _canvas.SetBaseImage(_canvas.Snapshot());
            }
            else
            {
                Entry.Logger.Warn("[DrawAndGuessMod] Failed to load PNG for history artwork editing.");
                CompleteHistoryEdit(null);
                return;
            }
        }
        if (!_privateDrawing)
        {
            DrawingNetSync.DeliverPending(this, _owner.NetId, _sessionId);
            SendBlankSettings();
            SendTimerSync(force: true);
        }
    }

    public override void _Process(double delta)
    {
        if (!_historyEdit && ShouldCloseForRunExit())
        {
            _finishing = true;
            _pendingCommands.Clear();
            Entry.Logger.Info(
                $"[DrawAndGuessMod] Closing drawing session {_sessionId} because the active run ended.");
            Complete(null);
            return;
        }

        if (_relicTarget != null && !_finishing)
        {
            UpdateRelicAssessment();
            AnimateRelicMatchBar(delta);
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

        // While the tracing search box is being edited, let its LineEdit consume
        // G / Ctrl+Z / Ctrl+Y / wheel instead of the drawing shortcuts.
        if (_tracingSearch != null && _tracingSearch.HasFocus())
        {
            return;
        }

        // In the history editor Escape cancels and must not bubble down to the
        // settings submenu (which is still open underneath the editor).
        if (_historyEdit &&
            !_colorPickerOverlay.Visible &&
            @event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            GetViewport().SetInputAsHandled();
            OnHistoryEditCancelPressed();
            return;
        }

        if (@event is InputEventMouseButton { Pressed: true } wheel &&
            wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown &&
            !_finishing &&
            !IsPointerOverTracingResults())
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
        // If the editor leaves the tree without going through CompleteHistoryEdit
        // (screen teardown, canvas-layer cleanup, ...), still deliver a cancel so
        // the artwork history list is refreshed on the settings page underneath.
        if (_historyEditClosed != null)
        {
            Action<byte[]?>? onClosed = _historyEditClosed;
            _historyEditClosed = null;
            Callable.From(() =>
            {
                try
                {
                    onClosed(null);
                }
                catch (Exception ex)
                {
                    Entry.Logger.Warn($"[DrawAndGuessMod] Failed to finish history artwork editing: {ex}");
                }
            }).CallDeferred();
        }
        if (!_historyEditCompletion.Task.IsCompleted && !_historyEditFinalizing)
        {
            _historyEditCompletion.TrySetResult(null);
        }
        if (ReferenceEquals(_active, this))
        {
            _active = null;
        }
        if (_historyEditLayer != null)
        {
            CanvasLayer layer = _historyEditLayer;
            _historyEditLayer = null;
            if (GodotObject.IsInstanceValid(layer))
            {
                layer.QueueFree();
            }
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
            _relicTarget != null
                ? Localized(
                    "固定为透明；右键可擦除画布内容",
                    "Locked to transparent; use the right mouse button to erase")
                : Localized(
                    "当前右键颜色；右键点击右侧色块即可替换",
                    "Current right mouse button color; right-click a swatch to replace it"));
        _rightColorButton.Disabled = _relicTarget != null;
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
                "调色盘：从色图选择或输入 RGB",
                "Color palette: choose from the color field or enter RGB values"),
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
                if (_relicTarget == null &&
                    @event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } &&
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
            Text = _historyEdit
                ? Localized(
                    "编辑完成后点击「确定」保存，或「取消」放弃修改。",
                    "Confirm to save your edits, or Cancel to discard them.")
                : _isChooser
                    ? Localized("完成后由你确认。", "Confirm the drawing when everyone is finished.")
                    : Localized(
                        "正在共同绘制，等待出牌者确认。",
                        "Drawing together. Waiting for the player who played the card to confirm."),
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore
        };
        column.AddChild(_status);

        HBoxContainer buttons = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center
        };
        buttons.AddThemeConstantOverride("separation", 12);
        column.AddChild(buttons);

        if (_historyEdit)
        {
            // History editor buttons: 确定 / 取消 / 撤销.
            _guessButton = CreateActionButton(
                Localized("确定", "Confirm"),
                new Color("176B72"),
                new Color("75F0E6"),
                primary: true);
            _guessButton.Disabled = !_isChooser ||
                                    !_receivedAuthoritativeBlankSettings ||
                                    _relicTarget != null;
            _guessButton.Pressed += OnGuessPressed;
            buttons.AddChild(_guessButton);

            _cancelButton = CreateActionButton(
                Localized("取消", "Cancel"),
                new Color("5B252B"),
                new Color("E47A78"));
            _cancelButton.Pressed += OnHistoryEditCancelPressed;
            buttons.AddChild(_cancelButton);

            Button undo = CreateActionButton(
                Localized("撤销", "Undo"),
                new Color("253D58"),
                new Color("79BCE8"));
            undo.TooltipText = Localized(
                $"撤回最近一次操作（Ctrl+Z；Ctrl+Y 重做），最多保留 {MaxUndoSteps} 步",
                $"Undo the most recent action (Ctrl+Z; Ctrl+Y to redo); up to {MaxUndoSteps} actions are retained");
            undo.Pressed += RequestUndo;
            buttons.AddChild(undo);
        }
        else
        {
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
        }

        AddPeekButton(backdrop, center);
        AddCanvasModeButton();
        if (!_historyEdit && _relicTarget == null && DrawAndGuessSettings.TracingEnabled)
        {
            BuildTracingPanel();
        }

        BuildColorPickerOverlay();
        if (_relicTarget != null)
        {
            BuildRelicWorkTitleOverlay();
        }
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
        if (_tracingPanel != null)
        {
            _tracingPanel.Visible = !peeking;
        }
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

        if (_historyEdit)
        {
            _finishing = true;
            _guessButton.Disabled = true;
            _cancelButton.Disabled = true;
            CompleteHistoryEdit(_canvas.ExportPng());
            return;
        }

        if (_relicTarget != null && !_relicDrawingConfirmed)
        {
            RelicArtAssessment? confirmedAssessment =
                RelicArtClassifier.AssessTarget(
                    _canvas.Snapshot(),
                    _relicTarget);
            if (confirmedAssessment?.IsAccepted != true)
            {
                _currentRelicAssessment = confirmedAssessment;
                UpdateRelicAssessmentState();
                return;
            }

            _currentRelicAssessment = confirmedAssessment;
            _relicDrawingConfirmed = true;
            UpdateRelicAssessmentState();
            OpenRelicWorkTitleEditor();
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
                RelicArtAssessment? finalAssessment = RelicArtClassifier.AssessTarget(
                    _canvas.Snapshot(),
                    _relicTarget);
                if (finalAssessment?.IsAccepted != true)
                {
                    _currentRelicAssessment = finalAssessment;
                    UpdateRelicAssessmentState();
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
            bool skipAddingToDeck = DrawingRunRules.GetBlankGeneratedCardSkipsDeck(_owner.RunState);
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
            TooltipText = colorName + (_relicTarget == null
                ? Localized(
                    "\n右键：设为右键颜色",
                    "\nRight-click: set as the right mouse button color")
                : string.Empty),
            CustomMinimumSize = new Vector2(30f, ColorButtonHeight),
            FocusMode = FocusModeEnum.None
        };
        ApplyColorButtonStyle(button, color);
        button.Pressed += () => SelectColor(color);
        button.GuiInput += @event =>
        {
            if (_relicTarget == null &&
                @event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
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
        color.A = 1f;
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
        if (_relicTarget != null)
        {
            return;
        }
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
        if (_relicTarget != null)
        {
            ApplyTransparencyMouseColorButtonStyle(_rightColorButton);
        }
        else
        {
            ApplyMouseColorButtonStyle(_rightColorButton, _rightColor);
        }
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
        button.AddThemeStyleboxOverride("disabled", CreateMouseColorButtonStyle(color, border, 2));
    }

    private static void ApplyTransparencyMouseColorButtonStyle(Button button)
    {
        const int width = 66;
        const int height = 47;
        const int cellSize = 8;
        const int borderWidth = 2;
        Color light = new("D8D8D8");
        Color dark = new("AFAFAF");
        Color border = new("8C938F");
        Image image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool isBorder =
                    x < borderWidth ||
                    y < borderWidth ||
                    x >= width - borderWidth ||
                    y >= height - borderWidth;
                image.SetPixel(
                    x,
                    y,
                    isBorder
                        ? border
                        : ((x / cellSize + y / cellSize) % 2 == 0 ? light : dark));
            }
        }

        StyleBoxTexture checker = new()
        {
            Texture = ImageTexture.CreateFromImage(image)
        };
        button.AddThemeFontSizeOverride("font_size", 15);
        button.AddThemeColorOverride("font_color", new Color("171B20"));
        button.AddThemeColorOverride("font_hover_color", new Color("171B20"));
        button.AddThemeColorOverride("font_pressed_color", new Color("171B20"));
        button.AddThemeColorOverride("font_disabled_color", new Color("171B20"));
        button.AddThemeStyleboxOverride("normal", checker);
        button.AddThemeStyleboxOverride("hover", checker);
        button.AddThemeStyleboxOverride("pressed", checker);
        button.AddThemeStyleboxOverride("disabled", checker);
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
                    (_relicTarget == null
                        ? Localized(
                            "\n右键：设为右键颜色",
                            "\nRight-click: set as the right mouse button color")
                        : string.Empty);
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
            EditAlpha = false,
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
            Text = "RGB",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        rgbTitle.AddThemeFontSizeOverride("font_size", 20);
        rgbColumn.AddChild(rgbTitle);
        _redInput = AddRgbInput(rgbColumn, "R");
        _greenInput = AddRgbInput(rgbColumn, "G");
        _blueInput = AddRgbInput(rgbColumn, "B");
        _redInput.ValueChanged += OnRgbInputChanged;
        _greenInput.ValueChanged += OnRgbInputChanged;
        _blueInput.ValueChanged += OnRgbInputChanged;

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
            1f);
        _colorPicker.Color = color;
        UpdateConfirmColorButton(color);
        _syncingColorInputs = false;
    }

    private void SyncColorInputs(Color color)
    {
        _syncingColorInputs = true;
        color.A = 1f;
        Color normalized = DrawingCommand.UnpackRgb(DrawingCommand.PackRgb(color));
        _colorPicker.Color = normalized;
        _redInput.Value = Mathf.RoundToInt(normalized.R * 255f);
        _greenInput.Value = Mathf.RoundToInt(normalized.G * 255f);
        _blueInput.Value = Mathf.RoundToInt(normalized.B * 255f);
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
        color.A = 1f;
        if (!_historyEdit && DrawingPaletteStore.TryRemember(_paletteOwner, color))
        {
            _customColors.Clear();
            _customColors.AddRange(
                DrawingPaletteStore.GetColors(_paletteOwner)
                    .Select(stored =>
                        new Color(stored.R, stored.G, stored.B, 1f)));
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
        DrawingScreen? active = GetMatchingActiveSession(message.OwnerId, message.SessionId);
        if (active == null)
        {
            return false;
        }

        active.ReceiveCommands(message, senderId);
        return true;
    }

    internal static bool TryReceiveFinal(DrawingFinalMessage message)
    {
        DrawingScreen? active = GetMatchingActiveSession(message.OwnerId, message.SessionId);
        if (active == null)
        {
            return false;
        }

        active.ReceiveFinal(message);
        return true;
    }

    internal static bool TryReceiveUndoRequest(DrawingUndoRequestMessage message, ulong senderId)
    {
        DrawingScreen? active = GetMatchingActiveSession(message.OwnerId, message.SessionId);
        if (active == null || !active._isChooser)
        {
            return false;
        }

        active.ApplyAuthoritativeUndo(senderId);
        return true;
    }

    internal static bool TryReceiveRedoRequest(DrawingRedoRequestMessage message, ulong senderId)
    {
        DrawingScreen? active = GetMatchingActiveSession(message.OwnerId, message.SessionId);
        if (active == null || !active._isChooser)
        {
            return false;
        }

        active.ApplyAuthoritativeRedo(senderId);
        return true;
    }

    internal static bool TryReceiveCanvasState(DrawingCanvasStateMessage message)
    {
        DrawingScreen? active = GetMatchingActiveSession(message.OwnerId, message.SessionId);
        if (active == null)
        {
            return false;
        }

        active.ReceiveCanvasState(message);
        return true;
    }

    internal static bool TryReceiveTimer(DrawingTimerSyncMessage message)
    {
        DrawingScreen? active = GetMatchingActiveSession(message.OwnerId, message.SessionId);
        if (active == null)
        {
            return false;
        }

        active.ReceiveTimer(message);
        return true;
    }

    internal static bool TryReceiveBlankSettings(DrawingBlankSettingsMessage message)
    {
        DrawingScreen? active = GetMatchingActiveSession(message.OwnerId, message.SessionId);
        if (active == null)
        {
            return false;
        }

        active.ReceiveBlankSettings(message);
        return true;
    }

    private static DrawingScreen? GetMatchingActiveSession(ulong ownerId, uint sessionId)
    {
        DrawingScreen? active = _active;
        return active != null &&
               GodotObject.IsInstanceValid(active) &&
               !active._historyEdit &&
               active._owner.NetId == ownerId &&
               active._sessionId == sessionId
            ? active
            : null;
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
            RecordHistoryCommand(
                _historyEdit ? HistoryEditSenderId : _owner.NetId,
                command);
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
            ApplyAuthoritativeUndo(_historyEdit ? 0ul : _owner.NetId);
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
            ApplyAuthoritativeRedo(_historyEdit ? 0ul : _owner.NetId);
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
        if (_completion.Task.IsCompleted)
        {
            return;
        }

        // TrySetResult resumes Blank.OnPlay synchronously. Release the active
        // slot first so Replay can open its next drawing session instead of
        // seeing this already-completed screen and rejecting the new session.
        if (ReferenceEquals(_active, this))
        {
            _active = null;
        }
        QueueFree();
        _completion.TrySetResult(result);
    }

    private void CompleteRelic(RelicDrawingResult? result)
    {
        if (_relicCompletion.TrySetResult(result))
        {
            if (result != null && DrawingNetSync.IsMultiplayer)
            {
                ShowRelicWaitingState();
            }
            else
            {
                QueueFree();
            }
        }
    }

    private void OnHistoryEditCancelPressed()
    {
        if (_finishing)
        {
            return;
        }

        _finishing = true;
        _guessButton.Disabled = true;
        _cancelButton.Disabled = true;
        CompleteHistoryEdit(null);
    }

    private void CompleteHistoryEdit(byte[]? pngBytes)
    {
        if (_historyEditCompletion.Task.IsCompleted)
        {
            return;
        }

        Action<byte[]?>? onClosed = _historyEditClosed;
        _historyEditClosed = null;
        _historyEditFinalizing = true;
        Node? root = GetTree()?.Root;
        if (ReferenceEquals(_active, this))
        {
            _active = null;
        }

        QueueFree();
        void FinishOnMainThread()
        {
            try
            {
                onClosed?.Invoke(pngBytes);
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn($"[DrawAndGuessMod] Failed to finish history artwork editing: {ex}");
            }
            finally
            {
                _historyEditCompletion.TrySetResult(pngBytes);
            }
        }

        if (root != null && GodotObject.IsInstanceValid(root))
        {
            Callable.From(FinishOnMainThread).CallDeferred();
        }
        else
        {
            FinishOnMainThread();
        }
    }

    private static DrawingCanvasMode DetectCanvasMode(byte[] pngBytes)
    {
        Image image = new();
        if (image.LoadPngFromBuffer(pngBytes) != Error.Ok)
        {
            return DrawingCanvasMode.Standard;
        }

        return (image.GetWidth(), image.GetHeight()) switch
        {
            (DrawingCanvas.AncientCanvasWidth, DrawingCanvas.AncientCanvasHeight) => DrawingCanvasMode.Ancient,
            (DrawingCanvas.RelicCanvasWidth, DrawingCanvas.RelicCanvasHeight) => DrawingCanvasMode.Relic,
            _ => DrawingCanvasMode.Standard
        };
    }

    private bool UsesCollaborativeNetworking => DrawingNetSync.IsMultiplayer && !_privateDrawing;

    private void BuildRelicTitleEditor(Container column)
    {
        _relicWorkTitleLabel = new Label
        {
            Text = _relicTarget?.Title.GetFormattedText() ??
                   Localized("无题作品", "Untitled Work"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _relicWorkTitleLabel.AddThemeFontSizeOverride("font_size", 30);
        column.AddChild(_relicWorkTitleLabel);
    }

    private void BuildRelicWorkTitleOverlay()
    {
        _relicWorkTitleOverlay = new Control
        {
            Name = "RelicWorkTitleOverlay",
            Visible = false,
            MouseFilter = MouseFilterEnum.Stop,
            ZIndex = 30
        };
        _relicWorkTitleOverlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_relicWorkTitleOverlay);

        CenterContainer center = new();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _relicWorkTitleOverlay.AddChild(center);

        PanelContainer panel = new()
        {
            CustomMinimumSize = new Vector2(520f, 190f)
        };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.045f, 0.06f, 0.085f, 0.86f),
            BorderColor = new Color("79BCE8"),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 12,
            CornerRadiusTopRight = 12,
            CornerRadiusBottomLeft = 12,
            CornerRadiusBottomRight = 12,
            ShadowColor = new Color(0f, 0f, 0f, 0.45f),
            ShadowSize = 10,
            ShadowOffset = new Vector2(0f, 5f)
        });
        center.AddChild(panel);

        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        panel.AddChild(margin);

        VBoxContainer content = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center
        };
        content.AddThemeConstantOverride("separation", 14);
        margin.AddChild(content);

        Label title = new()
        {
            Text = Localized("为作品命名", "Title Your Work"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 26);
        content.AddChild(title);

        _relicWorkTitleInput = new LineEdit
        {
            Text = _relicWorkTitleLabel?.Text ?? string.Empty,
            MaxLength = 32,
            CustomMinimumSize = new Vector2(440f, 48f),
            Alignment = HorizontalAlignment.Center,
            TooltipText = Localized(
                "鉴宝时只会显示这个名字；你可以用它伪装遗物。",
                "Only this title is shown during appraisal; you may use it to disguise the relic.")
        };
        _relicWorkTitleInput.AddThemeFontSizeOverride("font_size", 26);
        _relicWorkTitleInput.TextChanged += _ => UpdateRelicAssessmentState();
        _relicWorkTitleInput.TextSubmitted += _ => ConfirmRelicWorkTitleEditing();
        content.AddChild(_relicWorkTitleInput);

        Button confirm = CreateActionButton(
            Localized("确认并提交", "Confirm and Submit"),
            new Color("176B72"),
            new Color("75F0E6"),
            primary: true);
        confirm.CustomMinimumSize = new Vector2(220f, 48f);
        confirm.Pressed += ConfirmRelicWorkTitleEditing;
        content.AddChild(confirm);
    }

    private void OpenRelicWorkTitleEditor()
    {
        if (_relicWorkTitleInput == null ||
            _relicWorkTitleLabel == null ||
            _relicWorkTitleOverlay == null ||
            _editingRelicWorkTitle)
        {
            return;
        }

        if (_gFillHeld)
        {
            _gFillHeld = false;
            ActivateBrushTool();
        }
        _editingRelicWorkTitle = true;
        _relicWorkTitleInput.Text = _relicWorkTitleLabel.Text;
        _relicWorkTitleOverlay.Visible = true;
        _relicWorkTitleInput.GrabFocus();
        _relicWorkTitleInput.SelectAll();
        UpdateRelicAssessmentState();
    }

    private void ConfirmRelicWorkTitleEditing()
    {
        if (_relicWorkTitleInput == null ||
            _relicWorkTitleLabel == null ||
            _relicWorkTitleOverlay == null ||
            !_editingRelicWorkTitle)
        {
            return;
        }
        string editedTitle = _relicWorkTitleInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(editedTitle))
        {
            return;
        }

        _relicWorkTitleLabel.Text = editedTitle;
        _relicWorkTitleInput.ReleaseFocus();
        _relicWorkTitleOverlay.Visible = false;
        _editingRelicWorkTitle = false;
        UpdateRelicAssessmentState();
        OnGuessPressed();
    }

    private void ShowRelicWaitingState()
    {
        if (_relicWaitingOverlay != null &&
            GodotObject.IsInstanceValid(_relicWaitingOverlay))
        {
            return;
        }

        if (_relicWorkTitleOverlay != null)
        {
            _relicWorkTitleOverlay.Visible = false;
        }
        _editingRelicWorkTitle = false;
        _guessButton.Disabled = true;

        _relicWaitingOverlay = new Control
        {
            Name = "RelicWaitingOverlay",
            MouseFilter = MouseFilterEnum.Stop,
            ZIndex = 40
        };
        _relicWaitingOverlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_relicWaitingOverlay);

        ColorRect dimmer = new()
        {
            Color = new Color(0.01f, 0.015f, 0.025f, 0.34f),
            MouseFilter = MouseFilterEnum.Stop
        };
        dimmer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _relicWaitingOverlay.AddChild(dimmer);

        CenterContainer center = new();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _relicWaitingOverlay.AddChild(center);

        PanelContainer panel = new()
        {
            CustomMinimumSize = new Vector2(420f, 108f),
            MouseFilter = MouseFilterEnum.Stop
        };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.045f, 0.06f, 0.085f, 0.94f),
            BorderColor = new Color("79BCE8"),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 12,
            CornerRadiusTopRight = 12,
            CornerRadiusBottomLeft = 12,
            CornerRadiusBottomRight = 12,
            ShadowColor = new Color(0f, 0f, 0f, 0.5f),
            ShadowSize = 10,
            ShadowOffset = new Vector2(0f, 5f)
        });
        center.AddChild(panel);

        Label waiting = new()
        {
            Text = Localized("等待其他玩家", "Waiting for other players"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        waiting.AddThemeFontSizeOverride("font_size", 30);
        panel.AddChild(waiting);
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
            ? _relicTarget?.Title.GetFormattedText() ??
              Localized("无题作品", "Untitled Work")
            : title.Trim();
    }

    private void UpdateRelicAssessment()
    {
        if (_relicTarget == null || _finishing)
        {
            return;
        }

        _currentRelicAssessment = _canvas.IsBlank()
            ? null
            : RelicArtClassifier.AssessTarget(
                _canvas.Snapshot(),
                _relicTarget);
        UpdateRelicAssessmentState();
    }

    private void UpdateRelicAssessmentState()
    {
        if (_relicTarget == null)
        {
            return;
        }

        double similarity = _currentRelicAssessment?.SimilarityPercent ?? 0d;
        bool matches = _currentRelicAssessment?.IsAccepted == true;
        _relicMatchTargetSimilarity = similarity;
        _status.Text = string.Empty;
        _guessButton.Disabled =
            !matches ||
            _editingRelicWorkTitle ||
            string.IsNullOrWhiteSpace(_relicWorkTitleLabel?.Text);
    }

    private void AnimateRelicMatchBar(double delta)
    {
        if (_relicMatchBar == null)
        {
            return;
        }

        double animationWeight = 1d - Math.Exp(-Math.Max(0d, delta) * 10d);
        _relicMatchDisplayedSimilarity +=
            (_relicMatchTargetSimilarity - _relicMatchDisplayedSimilarity) *
            animationWeight;
        if (Math.Abs(
                _relicMatchDisplayedSimilarity -
                _relicMatchTargetSimilarity) < 0.05d)
        {
            _relicMatchDisplayedSimilarity = _relicMatchTargetSimilarity;
        }

        _relicMatchBar.Value = _relicMatchDisplayedSimilarity;
        bool displayedAsAccepted =
            _relicMatchDisplayedSimilarity >=
            RelicArtClassifier.RequiredSimilarityPercent;
        if (_relicMatchFillStyle != null)
        {
            float progressToThreshold = Mathf.Clamp(
                (float)(_relicMatchDisplayedSimilarity /
                        RelicArtClassifier.RequiredSimilarityPercent),
                0f,
                1f);
            _relicMatchFillStyle.BgColor = displayedAsAccepted
                ? new Color("58D68D")
                : new Color("F05A55").Lerp(
                    new Color("F4C95D"),
                    progressToThreshold);
        }
    }

    /// <summary>
    /// Builds the tracing reference panel as a transparent floating overlay
    /// anchored to the right edge of the screen, so the original drawing layout
    /// (canvas row, palette, tools) and the canvas-mode toggle are left untouched.
    /// </summary>
    private void BuildTracingPanel()
    {
        VBoxContainer panel = new()
        {
            Name = "TracingPanel",
            ZIndex = 10,
            MouseFilter = MouseFilterEnum.Pass
        };
        panel.SetAnchorsPreset(LayoutPreset.CenterRight);
        panel.OffsetLeft = -252f;
        panel.OffsetRight = -12f;
        panel.OffsetTop = -230f;
        panel.OffsetBottom = 230f;
        AddChild(panel);
        _tracingPanel = panel;
        panel.AddThemeConstantOverride("separation", 8);

        _tracingSearch = new LineEdit
        {
            PlaceholderText = Localized("搜索卡牌…", "Search cards..."),
            TooltipText = Localized(
                "输入卡牌名或 ID 进行搜索，选择一张卡牌作为临摹参考。",
                "Type a card name or ID to search; pick a card to use as a tracing reference."),
            CustomMinimumSize = new Vector2(0f, 34f)
        };
        _tracingSearch.TextChanged += OnTracingSearchTextChanged;
        panel.AddChild(_tracingSearch);

        ScrollContainer resultsScroll = new()
        {
            CustomMinimumSize = new Vector2(240f, 60f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        panel.AddChild(resultsScroll);
        _tracingResultsScroll = resultsScroll;
        _tracingResults = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(240f, 0f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _tracingResults.AddThemeConstantOverride("separation", 2);
        _tracingResults.Visible = false;
        resultsScroll.AddChild(_tracingResults);

        _tracingReference = new TextureRect
        {
            Visible = false,
            CustomMinimumSize = new Vector2(0f, 200f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Stop,
            TooltipText = Localized(
                "点击参考图取色：左键设为左键颜色，右键设为右键颜色",
                "Click the reference to sample its colors: LMB sets the left color, RMB sets the right color")
        };
        _tracingReference.GuiInput += OnTracingReferenceInput;
        panel.AddChild(_tracingReference);

        Label hint = new()
        {
            Text = Localized(
                "点击参考图可直接取色。",
                "Click the reference art to sample its colors."),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        panel.AddChild(hint);
    }

    private void OnTracingSearchTextChanged(string query)
    {
        if (_tracingResults == null)
        {
            return;
        }

        foreach (Node child in _tracingResults.GetChildren())
        {
            _tracingResults.RemoveChild(child);
            child.QueueFree();
        }

        string normalized = query.Trim();
        if (normalized.Length == 0)
        {
            _tracingResults.Visible = false;
            return;
        }

        if (!_tracingSearchPrepared)
        {
            StartPreparingTracingSearch();
            return;
        }

        int shown = 0;
        foreach (CardModel card in _searchableCards)
        {
            string title;
            try
            {
                title = card.Title;
            }
            catch
            {
                title = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = card.Id.Entry;
            }

            bool matches = title.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                           card.Id.Entry.Contains(normalized, StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                continue;
            }

            CardModel captured = card;
            Button result = new()
            {
                Text = title,
                TooltipText = $"{title}  ({card.Id.Entry})",
                ClipText = true,
                FocusMode = FocusModeEnum.None,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(240f, 34f)
            };
            result.AddThemeFontSizeOverride("font_size", 14);
            result.AddThemeColorOverride("font_color", Colors.White);
            result.AddThemeColorOverride("font_hover_color", Colors.White);
            result.AddThemeColorOverride("font_pressed_color", Colors.White);
            result.AddThemeColorOverride("font_outline_color", new Color("101820"));
            result.AddThemeConstantOverride("outline_size", 2);
            result.AddThemeStyleboxOverride("normal", CreateTracingSearchResultStyle("263545D9"));
            result.AddThemeStyleboxOverride("hover", CreateTracingSearchResultStyle("3D5870F2"));
            result.AddThemeStyleboxOverride("pressed", CreateTracingSearchResultStyle("1A2734F2"));
            result.Pressed += () => SelectTracingCard(captured);
            _tracingResults.AddChild(result);
            shown++;
            if (shown >= 50)
            {
                break;
            }
        }

        _tracingResults.Visible = shown > 0;
    }

    private bool IsPointerOverTracingResults()
    {
        return _tracingResultsScroll != null &&
               _tracingResultsScroll.Visible &&
               _tracingResultsScroll.GetGlobalRect().HasPoint(GetGlobalMousePosition());
    }

    private void StartPreparingTracingSearch()
    {
        if (_tracingSearchPreparing)
        {
            return;
        }

        _tracingSearchPreparing = true;
        _status.Text = Localized(
            "正在准备临摹卡牌列表…",
            "Preparing tracing card list...");
        _ = PrepareTracingSearchAsync();
    }

    private async Task PrepareTracingSearchAsync()
    {
        try
        {
            await CardArtClassifier.EnsureTrainedAsync();
            if (!GodotObject.IsInstanceValid(this) || _finishing)
            {
                return;
            }

            _searchableCards = CardArtClassifier.GetChallengeCandidates(_owner);
            _tracingSearchPrepared = true;
            OnTracingSearchTextChanged(_tracingSearch?.Text ?? string.Empty);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to build the tracing search pool: {ex}");
            _searchableCards = [];
            _tracingSearchPrepared = true;
        }
        finally
        {
            _tracingSearchPreparing = false;
        }
    }

    private static StyleBoxFlat CreateTracingSearchResultStyle(string color)
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(color),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8
        };
    }

    private void SelectTracingCard(CardModel card)
    {
        Image? image = CardArtClassifier.LoadCardPortraitImage(card);
        if (image == null || _tracingReference == null)
        {
            return;
        }

        if (!TryCreateTracingReference(image, out Image displayImage, out ImageTexture displayTexture))
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to create tracing reference texture for {card.Id.Entry}.");
            return;
        }

        _tracingReferenceImage = displayImage;
        _tracingReferenceTexture = displayTexture;
        _tracingReference.Texture = _tracingReferenceTexture;
        _tracingReference.Visible = true;
        _tracingReference.TooltipText = Localized(
                $"参考卡牌：{card.Title}",
                $"Reference: {card.Title}") +
            "\n" + Localized(
                "左键取色为左色，右键取色为右色",
                "LMB samples the left color, RMB samples the right color");
        _tracingResults!.Visible = false;
        _tracingSearch?.ReleaseFocus();
    }

    /// <summary>
    /// Third-party card portraits can be backed by compressed or atlas-owned GPU
    /// images. Copy their pixels into a standalone PNG before uploading so the
    /// tracing preview never retains an invalid external texture backing.
    /// </summary>
    private static bool TryCreateTracingReference(
        Image source,
        out Image displayImage,
        out ImageTexture displayTexture)
    {
        displayImage = new Image();
        displayTexture = null!;
        if (source.IsEmpty())
        {
            return false;
        }

        try
        {
            Image rgba = Image.CreateFromData(
                source.GetWidth(),
                source.GetHeight(),
                false,
                source.GetFormat(),
                source.GetData());
            if (rgba.IsCompressed() && rgba.Decompress() != Error.Ok)
            {
                return false;
            }

            rgba.Convert(Image.Format.Rgba8);
            byte[] pngBytes = rgba.SavePngToBuffer();
            if (displayImage.LoadPngFromBuffer(pngBytes) != Error.Ok)
            {
                return false;
            }

            displayTexture = ImageTexture.CreateFromImage(displayImage);
            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Debug($"[DrawAndGuessMod] Failed to isolate tracing reference texture: {ex.Message}");
            return false;
        }
    }

    private void OnTracingReferenceInput(InputEvent @event)
    {
        if (_tracingReferenceImage == null ||
            _tracingReference == null ||
            @event is not InputEventMouseButton { Pressed: true } button ||
            (button.ButtonIndex != MouseButton.Left && button.ButtonIndex != MouseButton.Right))
        {
            return;
        }

        Color? sampled = SampleTracingReference(button.Position, _tracingReference.Size);
        if (sampled == null)
        {
            return;
        }

        Color picked = sampled.Value;
        if (picked.A <= 0f)
        {
            return;
        }
        picked.A = 1f;
        if (button.ButtonIndex == MouseButton.Right)
        {
            SelectRightColor(picked);
        }
        else
        {
            SelectColor(picked);
        }

        _tracingReference.AcceptEvent();
    }

    private Color? SampleTracingReference(Vector2 localPosition, Vector2 controlSize)
    {
        Image image = _tracingReferenceImage!;
        int imageWidth = image.GetWidth();
        int imageHeight = image.GetHeight();
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return null;
        }

        float scale = Mathf.Min(controlSize.X / imageWidth, controlSize.Y / imageHeight);
        float drawWidth = imageWidth * scale;
        float drawHeight = imageHeight * scale;
        float offsetX = (controlSize.X - drawWidth) * 0.5f;
        float offsetY = (controlSize.Y - drawHeight) * 0.5f;
        float relativeX = (localPosition.X - offsetX) / drawWidth;
        float relativeY = (localPosition.Y - offsetY) / drawHeight;
        if (relativeX < 0f || relativeX > 1f || relativeY < 0f || relativeY > 1f)
        {
            return null;
        }

        int pixelX = Mathf.Clamp(Mathf.RoundToInt(relativeX * imageWidth), 0, imageWidth - 1);
        int pixelY = Mathf.Clamp(Mathf.RoundToInt(relativeY * imageHeight), 0, imageHeight - 1);
        return image.GetPixel(pixelX, pixelY);
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

        _relicMatchFillStyle = CreateTimerBarStyle(
            new Color("F05A55"),
            Colors.Transparent,
            0);
        _relicMatchBar = new ProgressBar
        {
            MinValue = 0d,
            MaxValue = 100d,
            Value = 0d,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(190f, 14f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        _relicMatchBar.AddThemeStyleboxOverride(
            "background",
            CreateTimerBarStyle(new Color("111722"), new Color("526070"), 2));
        _relicMatchBar.AddThemeStyleboxOverride("fill", _relicMatchFillStyle);
        references.AddChild(_relicMatchBar);

        ColorRect thresholdOutline = CreateRelicMatchThresholdMarker(
            new Color("291012"),
            6f);
        _relicMatchBar.AddChild(thresholdOutline);
        ColorRect thresholdMarker = CreateRelicMatchThresholdMarker(
            new Color("FF3038"),
            3f);
        _relicMatchBar.AddChild(thresholdMarker);
    }

    private static ColorRect CreateRelicMatchThresholdMarker(Color color, float width)
    {
        float threshold = (float)(
            RelicArtClassifier.RequiredSimilarityPercent / 100d);
        return new ColorRect
        {
            Color = color,
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = threshold,
            AnchorRight = threshold,
            AnchorTop = 0f,
            AnchorBottom = 1f,
            OffsetLeft = -width * 0.5f,
            OffsetRight = width * 0.5f,
            OffsetTop = -3f,
            OffsetBottom = 3f
        };
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
