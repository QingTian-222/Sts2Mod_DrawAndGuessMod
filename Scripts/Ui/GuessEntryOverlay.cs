using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Guess;
using DrawAndGuessMod.Scripts.Localization;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;

namespace DrawAndGuessMod.Scripts.Ui;

/// <summary>
/// 猜测端输入界面：纯 C# 动态实例化（零 .tscn 依赖）。
/// 展示画作 PNG + LineEdit 输入框；输入过程中在本地卡牌库模糊检索，
/// 确认后通过 <see cref="GuessPhaseCoordinator.SubmitGuess"/> 回传 CardId 并销毁自身。
/// </summary>
public partial class GuessEntryOverlay : Control
{
    private const float PanelWidth = 460f;

    private readonly TaskCompletionSource<bool> _submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Player _owner = null!;
    private uint _sessionId;
    private double _timeLeft;
    private bool _done;
    private bool _queueOnly;
    private string? _pendingGuess;
    private string _currentBestCardId = string.Empty;

    private Label _countdownLabel = null!;
    private Label _statusLabel = null!;
    private LineEdit _input = null!;
    private VBoxContainer _column = null!;
    private VBoxContainer _candidateList = null!;
    private Button _skipButton = null!;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 4096;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        BuildUi();
    }

    /// <summary>绑定会话上下文并展示画作。必须在 AddChild 之后调用。</summary>
    public void Bind(Player owner, uint sessionId, byte[] pngBytes, double timeoutSeconds)
    {
        _owner = owner;
        _sessionId = sessionId;
        _timeLeft = timeoutSeconds > 0d ? timeoutSeconds : 45d;
        SetImage(pngBytes);
        UpdateCountdownLabel();
        _input.GrabFocus();
    }

    /// <summary>
    /// 轮询模式绑定（供 DrawGuessSpectatorLoop）：不自行回传网络包，
    /// 玩家的猜测通过 <see cref="TakePendingGuess"/> 由后台循环取出并提交。
    /// </summary>
    public void BindPolling(Player owner, double timeoutSeconds)
    {
        _queueOnly = true;
        _owner = owner;
        _timeLeft = timeoutSeconds > 0d ? timeoutSeconds : 45d;
        UpdateCountdownLabel();
        _input.GrabFocus();
    }

    /// <summary>轮询模式下取出玩家已确认的猜测；无则返回 null。空串 = 弃权。</summary>
    public string? TakePendingGuess()
    {
        string? guess = _pendingGuess;
        _pendingGuess = null;
        return guess;
    }

    /// <summary>轮询模式下锁定输入并显示等待文案。</summary>
    public void LockToWaiting(string message)
    {
        _done = true;
        LockInput(message);
        _submitted.TrySetResult(true);
    }

    /// <summary>轮询模式下的本地兜底 CardId（当前最佳匹配；无匹配则为空串弃权）。</summary>
    public string CurrentBestCardId => _currentBestCardId;

    /// <summary>等待玩家提交猜测或弃权（节点随后即可销毁）。</summary>
    public Task WaitForSubmitAsync()
    {
        return _submitted.Task;
    }

    public override void _Process(double delta)
    {
        if (_done)
        {
            return;
        }

        _timeLeft -= delta;
        if (_timeLeft <= 0d)
        {
            _timeLeft = 0d;
            if (_queueOnly)
            {
                // 轮询模式：倒计时结束由后台循环兜底提交，这里只展示状态。
                _statusLabel.Text = ModText.Get("时间到，等待结算……", "Time's up, waiting for result...");
            }
            else
            {
                // 绘画者端倒计时是权威；本地到时只锁定输入，等待最终裁定。
                LockInput(ModText.Get("时间到，等待绘画者结算……", "Time's up, waiting for host to finalize..."));
            }
        }

        UpdateCountdownLabel();
    }

    public override void _ExitTree()
    {
        _submitted.TrySetResult(true);
    }

    /// <summary>展示画作 PNG（公开，供轮询模式的后台循环调用）。</summary>
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
            CustomMinimumSize = new Vector2(PanelWidth - 48f, 220f),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter
        };
        // 插入到标题/倒计时之后、输入框之前。
        _column.AddChild(rect);
        _column.MoveChild(rect, 2);
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
            CustomMinimumSize = new Vector2(PanelWidth, 0f)
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
            Text = ModText.Get("你画我猜", "Draw & Guess"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 26);
        column.AddChild(title);

        _countdownLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _countdownLabel.AddThemeFontSizeOverride("font_size", 15);
        column.AddChild(_countdownLabel);

        _input = new LineEdit
        {
            PlaceholderText = ModText.Get("输入卡牌名称，从下方候选中确认……", "Type a card name, then select from the list below..."),
            CustomMinimumSize = new Vector2(0f, 40f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _input.TextChanged += OnTextChanged;
        _input.TextSubmitted += OnTextSubmitted;
        column.AddChild(_input);

        _candidateList = new VBoxContainer();
        _candidateList.AddThemeConstantOverride("separation", 4);
        column.AddChild(_candidateList);

        _statusLabel = new Label
        {
            Text = ModText.Get("回车提交最佳匹配，或点击候选确认。", "Press Enter to submit the top match, or click a candidate."),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 13);
        column.AddChild(_statusLabel);

        _skipButton = new Button { Text = ModText.Get("放弃猜测", "Skip") };
        _skipButton.Pressed += OnSkipPressed;
        column.AddChild(_skipButton);
    }

    private void OnTextChanged(string newText)
    {
        foreach (Node child in _candidateList.GetChildren())
        {
            child.QueueFree();
        }

        IReadOnlyList<CardSearchHit> hits = CardFuzzySearch.Search(newText, _owner);
        _currentBestCardId = hits.Count > 0 ? hits[0].CardId : string.Empty;
        foreach (CardSearchHit hit in hits)
        {
            string cardId = hit.CardId;
            Button candidate = new()
            {
                Text = hit.Title,
                TooltipText = cardId,
                Alignment = HorizontalAlignment.Left,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            candidate.Pressed += () => Submit(cardId, hit.Title);
            _candidateList.AddChild(candidate);
        }
    }

    private void OnTextSubmitted(string text)
    {
        IReadOnlyList<CardSearchHit> hits = CardFuzzySearch.Search(text, _owner);
        if (hits.Count == 0)
        {
            _statusLabel.Text = ModText.Get("没有匹配的卡牌，请换个关键词。", "No matching card found. Try a different keyword.");
            return;
        }

        Submit(hits[0].CardId, hits[0].Title);
    }

    private void OnSkipPressed()
    {
        if (_queueOnly)
        {
            _pendingGuess = string.Empty;
        }

        Finish(ModText.Get("已放弃猜测，等待结算……", "Skipped, waiting for result..."));
    }

    private void Submit(string cardId, string title)
    {
        if (_done)
        {
            return;
        }

        if (_queueOnly)
        {
            _pendingGuess = cardId;
            Finish(ModText.Get("已提交猜测：{0}，等待结算……", "Guess submitted: {0}, waiting for result...").Replace("{0}", title));
            return;
        }

        GuessPhaseCoordinator.SubmitGuess(_owner.NetId, _sessionId, cardId);
        Finish(ModText.Get("已提交猜测：{0}，等待结算……", "Guess submitted: {0}, waiting for result...").Replace("{0}", title));
    }

    private void Finish(string message)
    {
        _done = true;
        LockInput(message);
        _submitted.TrySetResult(true);
    }

    private void LockInput(string message)
    {
        _input.Editable = false;
        _skipButton.Disabled = true;
        foreach (Node child in _candidateList.GetChildren())
        {
            child.QueueFree();
        }

        _statusLabel.Text = message;
    }

    private void UpdateCountdownLabel()
    {
        _countdownLabel.Text = ModText.Get(
            $"剩余时间：{Mathf.CeilToInt((float)_timeLeft)} 秒",
            $"Time Left: {Mathf.CeilToInt((float)_timeLeft)} s");
    }
}
