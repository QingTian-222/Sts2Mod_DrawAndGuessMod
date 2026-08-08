using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Ai;
using DrawAndGuessMod.Scripts.Cards;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.Ui;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace DrawAndGuessMod.Scripts.Config;

public enum GuessCardPoolScope
{
    AllCards = 0,
    CurrentCharacter = 1
}

public enum RecognitionModelAccuracy
{
    Waku = 0,
    Jibao = 1,
    SketchAdapter = 2
}

internal static class DrawAndGuessSettings
{
    private const string DataKey = "settings";
    private const string FileName = "settings.json";
    private const string AiSettingsPageId = "ai_settings";
    private const string GameSettingsPageId = "game_settings";
    private const string ArtworkHistoryPageId = "artwork_history";
    private const ModSettingsHostSurface SettingsHostSurfaces =
        ModSettingsHostSurface.MainMenu |
        ModSettingsHostSurface.RunPause |
        ModSettingsHostSurface.CombatPause;
    private static bool _pretraining;
    private static string _pretrainingStatusChinese = string.Empty;
    private static string _pretrainingStatusEnglish = string.Empty;
    private static double _pretrainingProgress;
    private static ProgressBar? _pretrainingProgressBar;
    private static Label? _pretrainingProgressLabel;
    private static TextureRect? _pretrainingThumbnailRect;
    private static readonly ImageTexture?[] PretrainingThumbnailTextures = new ImageTexture?[5];
    private static readonly Image?[] PretrainingThumbnailUploadSources = new Image?[5];
    private static int _pretrainingThumbnailTextureIndex = -1;
    private static bool _hasPretrainingThumbnail;
    private static IReadOnlyList<CandidatePoolInfo>? _detectedCandidatePools;
    private static string _candidatePoolDetectionError = string.Empty;
    private static VBoxContainer? _candidatePoolControls;
    private static string PretrainingStatus => Localized(_pretrainingStatusChinese, _pretrainingStatusEnglish);

    internal static bool IsPretraining => _pretraining;
    internal static double PretrainingProgress => _pretrainingProgress;
    internal static string CurrentPretrainingStatus => PretrainingStatus;
    internal static Texture2D? CurrentPretrainingThumbnail =>
        _hasPretrainingThumbnail && _pretrainingThumbnailTextureIndex >= 0
            ? PretrainingThumbnailTextures[_pretrainingThumbnailTextureIndex]
            : null;
    internal static event Action? PretrainingChanged;

    private static readonly IModSettingsValueBinding<int> CardPoolScopeBinding =
        ModSettingsBindings.Global<SettingsData, int>(
            Entry.ModId,
            DataKey,
            data => data.CardPoolScope,
            (data, value) => data.CardPoolScope = Math.Clamp(value, 0, 1));

    private static readonly IModSettingsValueBinding<bool> IncludeMultiplayerCardsBinding =
        ModSettingsBindings.Global<SettingsData, bool>(
            Entry.ModId,
            DataKey,
            data => data.IncludeMultiplayerCards,
            (data, value) => data.IncludeMultiplayerCards = value);

    private static readonly IModSettingsValueBinding<bool> ExcludePreviouslySelectedBlankCardsBinding =
        ModSettingsBindings.Global<SettingsData, bool>(
            Entry.ModId,
            DataKey,
            data => data.ExcludePreviouslySelectedBlankCards,
            (data, value) => data.ExcludePreviouslySelectedBlankCards = value);

    private static readonly IModSettingsValueBinding<bool> BlankGeneratedCardSkipsDeckBinding =
        ModSettingsBindings.Global<SettingsData, bool>(
            Entry.ModId,
            DataKey,
            data => data.BlankGeneratedCardSkipsDeck,
            (data, value) => data.BlankGeneratedCardSkipsDeck = value);

    private static readonly IModSettingsValueBinding<int> RecognitionModelAccuracyBinding =
        ModSettingsBindings.Global<SettingsData, int>(
            Entry.ModId,
            DataKey,
            data => data.RecognitionModelAccuracy,
            (data, value) => data.RecognitionModelAccuracy = Math.Clamp(value, 0, 2));

    private static readonly IModSettingsValueBinding<int> DrawingTimeLimitPresetBinding =
        ModSettingsBindings.Global<SettingsData, int>(
            Entry.ModId,
            DataKey,
            data => data.DrawingTimeLimitPreset,
            (data, value) => data.DrawingTimeLimitPreset = NormalizeDrawingTimeLimitPreset(value));

    private static readonly IModSettingsValueBinding<int> CustomDrawingTimeLimitSecondsBinding =
        ModSettingsBindings.Global<SettingsData, int>(
            Entry.ModId,
            DataKey,
            data => data.CustomDrawingTimeLimitSeconds,
            (data, value) => data.CustomDrawingTimeLimitSeconds = Math.Clamp(value, 1, 600));

    private static readonly IModSettingsValueBinding<bool> TracingEnabledBinding =
        ModSettingsBindings.Global<SettingsData, bool>(
            Entry.ModId,
            DataKey,
            data => data.TracingEnabled,
            (data, value) => data.TracingEnabled = value);

    private static readonly IModSettingsValueBinding<bool> TreasureRoomRelicDrawingEnabledBinding =
        ModSettingsBindings.Global<SettingsData, bool>(
            Entry.ModId,
            DataKey,
            data => data.TreasureRoomRelicDrawingEnabled,
            (data, value) => data.TreasureRoomRelicDrawingEnabled = value);

    public static GuessCardPoolScope CardPoolScope
    {
        get
        {
            try
            {
                return (GuessCardPoolScope)Math.Clamp(CardPoolScopeBinding.Read(), 0, 1);
            }
            catch
            {
                return GuessCardPoolScope.AllCards;
            }
        }
    }

    public static bool IncludeMultiplayerCards
    {
        get
        {
            try
            {
                return IncludeMultiplayerCardsBinding.Read();
            }
            catch
            {
                return true;
            }
        }
    }

    public static bool ExcludePreviouslySelectedBlankCards
    {
        get
        {
            try
            {
                return ExcludePreviouslySelectedBlankCardsBinding.Read();
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool BlankGeneratedCardSkipsDeck
    {
        get
        {
            try
            {
                return BlankGeneratedCardSkipsDeckBinding.Read();
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool TracingEnabled
    {
        get
        {
            try
            {
                return TracingEnabledBinding.Read();
            }
            catch
            {
                return false;
            }
        }
    }

    public static RecognitionModelAccuracy RecognitionModelAccuracy
    {
        get
        {
            try
            {
                return (RecognitionModelAccuracy)Math.Clamp(RecognitionModelAccuracyBinding.Read(), 0, 2);
            }
            catch
            {
                return RecognitionModelAccuracy.Jibao;
            }
        }
    }

    public static HashSet<ModelId> GetCardIdsExcludedByAdvancedPoolSettings()
    {
        try
        {
            List<CandidatePoolInfo> pools = GetCandidateCardPools();
            HashSet<ModelId> enabledCardIds = new();
            HashSet<ModelId> disabledCardIds = new();
            foreach (CandidatePoolInfo poolInfo in pools)
            {
                HashSet<ModelId> target = IsCardPoolEnabled(poolInfo.Pool)
                    ? enabledCardIds
                    : disabledCardIds;
                target.UnionWith(poolInfo.Pool.AllCardIds);
            }

            disabledCardIds.ExceptWith(enabledCardIds);
            return disabledCardIds;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to apply candidate card-pool settings: {ex.Message}");
            return new HashSet<ModelId>();
        }
    }

    public static void Register()
    {
        try
        {
            using (RitsuLibFramework.BeginModDataRegistration(Entry.ModId, false))
            {
                RitsuLibFramework.GetDataStore(Entry.ModId).Register(
                    DataKey,
                    FileName,
                    SaveScope.Global,
                    () => new SettingsData());
            }

            RitsuLibFramework.GetDataStore(Entry.ModId).InitializeGlobal();
            RitsuLibFramework.RegisterModSettings(Entry.ModId, BuildSettingsPage);
            RitsuLibFramework.RegisterModSettings(
                Entry.ModId,
                BuildAiSettingsPage,
                AiSettingsPageId);
            RitsuLibFramework.RegisterModSettings(
                Entry.ModId,
                BuildGameSettingsPage,
                GameSettingsPageId);
            RitsuLibFramework.RegisterModSettings(
                Entry.ModId,
                BuildArtworkHistoryPage,
                ArtworkHistoryPageId);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to register settings: {ex.Message}");
            GD.PushWarning($"[DrawAndGuessMod] Failed to register settings: {ex.Message}");
        }
    }

    private static void BuildSettingsPage(ModSettingsPageBuilder page)
    {
        page
            .WithModDisplayName(LocalizedText("你画瓦猜", "Draw & Guess"))
            .WithTitle(LocalizedText("你画瓦猜", "Draw & Guess"))
            .WithSortOrder(0)
            .WithDescription(LocalizedText(
                "选择一个分类查看详细设置。",
                "Choose a category to view its settings."))
            .WithVisibleOnHostSurfaces(SettingsHostSurfaces)
            .AddSection("settings_categories", section => section
                .WithTitle(LocalizedText("设置分类", "Settings Categories"))
                .AddSubpage(
                    "open_ai_settings",
                    LocalizedText("AI 设置", "AI Settings"),
                    AiSettingsPageId,
                    LocalizedText("打开", "Open"),
                    LocalizedText(
                        "识别缓存、识别范围、多人卡牌、模型选择和候选卡池高级选项。",
                        "Recognition cache, scope, multiplayer cards, model selection, and advanced candidate-pool options."))
                .AddSubpage(
                    "open_game_settings",
                    LocalizedText("游戏设置", "Game Settings"),
                    GameSettingsPageId,
                    LocalizedText("打开", "Open"),
                    LocalizedText(
                        "调整“空白”、作画时间限制和参考图等游戏体验选项。",
                        "Configure Blank, drawing time limits, reference images, and other gameplay options."))
                .AddSubpage(
                    "open_artwork_history",
                    LocalizedText("历史画作", "Artwork History"),
                    ArtworkHistoryPageId,
                    LocalizedText("打开", "Open"),
                    LocalizedText(
                        "查看所有已完成的卡牌及遗物画作。",
                        "Browse all completed card and relic drawings.")));
    }

    public static bool TreasureRoomRelicDrawingEnabled
    {
        get
        {
            try
            {
                return TreasureRoomRelicDrawingEnabledBinding.Read();
            }
            catch
            {
                return true;
            }
        }
    }

    private static void BuildAiSettingsPage(ModSettingsPageBuilder page)
    {
        if (string.IsNullOrEmpty(_pretrainingStatusChinese))
        {
            SetPretrainingStatus(
                "读取当前安装的原版及模组卡牌图片并建立识别缓存，使新卡牌能够参与猜测，并减少游戏中的等待时间。",
                "Scan images from the base game and installed card mods to build a recognition cache. This lets new cards participate in guesses and reduces in-game waiting.");
        }

        page
            .AsChildOf(Entry.ModId)
            .WithTitle(LocalizedText("AI 设置", "AI Settings"))
            .WithSortOrder(10)
            .WithDescription(LocalizedText(
                "管理识别模型、缓存和 AI 候选范围。",
                "Manage recognition models, caches, and AI candidate pools."))
            .WithVisibleOnHostSurfaces(SettingsHostSurfaces)
            .AddSection("model_training", section => section
                .WithTitle(LocalizedText("卡牌识别缓存", "Card Recognition Cache"))
                .AddCustom(
                    "pretraining_progress",
                    LocalizedText("卡牌扫描进度", "Card Scan Progress"),
                    BuildPretrainingProgressControl,
                    LocalizedText(
                        "显示当前卡牌图片的分析进度。",
                        "Shows progress while card images are being analyzed."),
                    () => _pretraining)
                .AddButton(
                    "pretrain_current_cards",
                    LocalizedText("识别缓存", "Recognition Cache"),
                    DynamicText(() => _pretraining
                        ? Localized("正在分析卡牌图片……", "Analyzing card images...")
                        : Localized("扫描卡牌并建立识别缓存", "Scan Cards and Build Cache")),
                    StartPretraining,
                    ModSettingsButtonTone.Accent,
                    DynamicText(() => PretrainingStatus))
                .WithEntryEnabledWhen("pretrain_current_cards", () => !_pretraining))
            .AddSection("guess_pool", section => section
                .WithTitle(LocalizedText("AI 候选牌池", "AI Candidate Pool"))
                .AddChoice(
                    "card_pool_scope",
                    LocalizedText("识别范围", "Recognition Scope"),
                    CardPoolScopeBinding,
                    new[]
                    {
                        new ModSettingsChoiceOption<int>((int)GuessCardPoolScope.AllCards, LocalizedText("全卡池", "All Cards")),
                        new ModSettingsChoiceOption<int>((int)GuessCardPoolScope.CurrentCharacter, LocalizedText("当前角色卡池", "Current Character"))
                    },
                    LocalizedText(
                        "全卡池：从所有可见卡牌中猜测。当前角色卡池：只从当前角色自己的卡牌中猜测。",
                        "All Cards: guess from every visible card. Current Character: only guess cards from the current character's card pool."),
                    ModSettingsChoicePresentation.Dropdown)
                .AddToggle(
                    "include_multiplayer_cards",
                    LocalizedText("启用多人卡牌", "Include Multiplayer Cards"),
                    IncludeMultiplayerCardsBinding,
                    LocalizedText(
                        "开启后，AI 候选中可以包含仅限多人模式的卡牌；关闭时会排除这些牌。",
                        "Allow multiplayer-only cards to appear among AI candidates. Disable this to exclude them."),
                    () => true)
                .AddChoice(
                    "recognition_model_accuracy",
                    LocalizedText("识别模型准确度", "Recognition Model"),
                    RecognitionModelAccuracyBinding,
                    new[]
                    {
                        new ModSettingsChoiceOption<int>((int)RecognitionModelAccuracy.Waku, LocalizedText("瓦库", "VAKUU")),
                        new ModSettingsChoiceOption<int>((int)RecognitionModelAccuracy.Jibao, LocalizedText("鸡煲", "Defect")),
                        new ModSettingsChoiceOption<int>(
                            (int)RecognitionModelAccuracy.SketchAdapter,
                            LocalizedText("自训练适配器（实验性）", "Trained Adapter (Experimental)"))
                    },
                    LocalizedText(
                        "瓦库：基础模型。鸡煲：更加智能的神经网络。自训练适配器：或许对简笔画有更高识别性。",
                        "VAKUU: basic model. Defect: better nn model. Trained Adapter: Higher recognition of simple drawings."),
                    ModSettingsChoicePresentation.Dropdown))
            .AddSection("advanced_candidate_pools", section => section
                .WithTitle(LocalizedText("高级选项", "Advanced Options"))
                .WithDescription(LocalizedText(
                    "单独关闭不希望参与识别的候选卡池。新检测到的卡池默认开启。",
                    "Disable individual card pools that should not participate in recognition. Newly detected pools are enabled by default."))
                .Collapsible(true)
                .AddButton(
                    "detect_candidate_card_pools",
                    LocalizedText("检测卡池", "Detect Card Pools"),
                    LocalizedText("检测/重新检测已加载的卡池", "Detect / Re-detect Loaded Card Pools"),
                    DetectCandidateCardPools,
                    ModSettingsButtonTone.Accent,
                    LocalizedText(
                        "在所有模组加载完成后读取候选卡池，并刷新下方的独立开关列表。",
                        "Read candidate card pools after all mods have loaded and refresh the individual toggles below."))
                .AddCustom(
                    "candidate_card_pools",
                    LocalizedText("候选卡池", "Candidate Card Pools"),
                    BuildCandidatePoolControls,
                    LocalizedText(
                        "关闭的卡池不会出现在识别结果中；已经建立的识别缓存不会被删除。",
                        "Disabled pools will not appear in recognition results. Existing recognition cache data is kept."),
                    () => true));
    }

    private static void BuildGameSettingsPage(ModSettingsPageBuilder page)
    {
        page
            .AsChildOf(Entry.ModId)
            .WithTitle(LocalizedText("游戏设置", "Game Settings"))
            .WithSortOrder(20)
            .WithDescription(LocalizedText(
                "调整“空白”和绘画界面的游戏体验。",
                "Configure Blank and the drawing interface."))
            .WithVisibleOnHostSurfaces(SettingsHostSurfaces)
            .AddSection("blank_rules", section => section
                .WithTitle(LocalizedText("空白", "Blank"))
                .AddToggle(
                    "exclude_previously_selected_blank_cards",
                    LocalizedText(
                        "空白不再猜测已选择的卡牌",
                        "Exclude Cards Previously Selected by Blank"),
                    ExcludePreviouslySelectedBlankCardsBinding,
                    LocalizedText(
                        "开启后，本局中任何玩家通过“空白”选择过的卡牌都不会再次出现在“空白”的候选中。多人模式共享记录并使用房主设置。默认开启。",
                        "Previously selected Blank cards will no longer appear among Blank's candidates. Multiplayer shares one history and uses the host's setting. Enabled by default."),
                    () => true)
                .AddToggle(
                    "blank_generated_card_skips_deck",
                    LocalizedText(
                        "空白生成的卡片不进入卡组",
                        "Do Not Add Blank's Card to Deck"),
                    BlankGeneratedCardSkipsDeckBinding,
                    LocalizedText(
                        "开启后，“空白”生成的卡牌只加入被指定玩家的手牌，不会加入卡组。默认关闭。",
                        "When enabled, the card generated by Blank is added only to the targeted player's Hand, not their Deck. Disabled by default."),
                    () => true)
                .AddChoice(
                    "drawing_time_limit",
                    LocalizedText("作画时间限制", "Drawing Time Limit"),
                    DrawingTimeLimitPresetBinding,
                    new[]
                    {
                        new ModSettingsChoiceOption<int>(0, LocalizedText("关闭", "Off")),
                        new ModSettingsChoiceOption<int>(15, LocalizedText("15 秒", "15 Seconds")),
                        new ModSettingsChoiceOption<int>(30, LocalizedText("30 秒", "30 Seconds")),
                        new ModSettingsChoiceOption<int>(60, LocalizedText("60 秒", "60 Seconds")),
                        new ModSettingsChoiceOption<int>(90, LocalizedText("90 秒", "90 Seconds")),
                        new ModSettingsChoiceOption<int>(120, LocalizedText("120 秒", "120 Seconds")),
                        new ModSettingsChoiceOption<int>(-1, LocalizedText("自定义", "Custom"))
                    },
                    LocalizedText(
                        "限制普通“空白”的作画时间。倒计时结束时会自动确认画作；多人模式使用房主的设置。默认关闭。",
                        "Limit drawing time for the regular Blank card. The drawing is confirmed automatically when time expires. Multiplayer uses the host's setting. Disabled by default."),
                    ModSettingsChoicePresentation.Dropdown)
                .AddIntSlider(
                    "custom_drawing_time_limit_seconds",
                    LocalizedText("自定义秒数", "Custom Seconds"),
                    CustomDrawingTimeLimitSecondsBinding,
                    1,
                    600,
                    1,
                    value => Localized($"{value} 秒", $"{value} sec"),
                    LocalizedText(
                        "自定义普通“空白”的作画时间，范围为 1 至 600 秒。",
                        "Set a custom drawing time for the regular Blank card, from 1 to 600 seconds."))
                .WithEntryVisibleWhen(
                    "custom_drawing_time_limit_seconds",
                    () => NormalizeDrawingTimeLimitPreset(DrawingTimeLimitPresetBinding.Read()) == -1))
            .AddSection("drawing_ui", section => section
                .WithTitle(LocalizedText("绘画界面", "Drawing Interface"))
                .AddToggle(
                    "treasure_room_relic_drawing_enabled",
                    LocalizedText("宝箱房画遗物", "Draw Relics in Treasure Rooms"),
                    TreasureRoomRelicDrawingEnabledBinding,
                    LocalizedText(
                        "多人模式下，进入宝箱房后每名玩家会绘制一个由原版宝箱逻辑生成的遗物，全部提交后自动开箱。开启时不会出现“鉴宝大会”事件。默认开启；本局最终使用房主在涅奥界面确认的设置。",
                        "In multiplayer, each player draws a relic generated by the vanilla treasure-room logic. The chest opens after every drawing is submitted. Relic Appraisal Fair will not appear while enabled. Enabled by default; the host's Neow setting controls the current run."),
                    () => true)
                .WithEntryVisibleWhen(
                    "treasure_room_relic_drawing_enabled",
                    IsCurrentRunMultiplayer)
                .AddToggle(
                    "tracing_enabled",
                    LocalizedText("参考图", "Reference Image"),
                    TracingEnabledBinding,
                    LocalizedText(
                        "将“切换画布”替换为参考图按钮。点击后可从原版卡牌图鉴界面选择一张卡牌；鼠标中键点击参考图可吸色并设为左键颜色。",
                        "Replace Switch Canvas with a reference button. Choose a card from the game's card library, then middle-click its art to sample a color for LMB."),
                    () => true));
    }

    private static void BuildArtworkHistoryPage(ModSettingsPageBuilder page)
    {
        page
            .AsChildOf(Entry.ModId)
            .WithTitle(LocalizedText("历史画作", "Artwork History"))
            .WithSortOrder(30)
            .WithDescription(LocalizedText(
                "查看已完成的「空白」、画廊挑战和遗物鉴定画作。",
                "Browse completed Blank, gallery challenge, and relic appraisal drawings."))
            .WithVisibleOnHostSurfaces(SettingsHostSurfaces)
            .AddSection("artworks", section => section
                .WithTitle(LocalizedText("画作", "Artworks"))
                .AddCustom(
                    "artwork_entries",
                    LocalizedText("历史记录", "History"),
                    ArtworkHistoryViewer.BuildSettingsControl,
                    LocalizedText(
                        "按完成时间从新到旧排列。重命名、编辑和删除会立即刷新列表。",
                        "Entries are ordered newest first. Rename, edit, and delete refresh the list immediately.")));
    }

    private static IModSettingsValueBinding<bool> CreateCandidatePoolBinding(string poolKey)
    {
        return ModSettingsBindings.Global<SettingsData, bool>(
            Entry.ModId,
            DataKey,
            data => data.DisabledCardPools?.Contains(poolKey, StringComparer.Ordinal) != true,
            (data, enabled) =>
            {
                data.DisabledCardPools ??= new List<string>();
                data.DisabledCardPools.RemoveAll(key => string.Equals(key, poolKey, StringComparison.Ordinal));
                if (!enabled)
                {
                    data.DisabledCardPools.Add(poolKey);
                }
            });
    }

    private static void DetectCandidateCardPools(IModSettingsUiActionHost host)
    {
        try
        {
            _detectedCandidatePools = GetCandidateCardPools();
            _candidatePoolDetectionError = string.Empty;
            Entry.Logger.Info(
                $"[DrawAndGuessMod] Detected {_detectedCandidatePools.Count} candidate card pools: " +
                string.Join(", ", _detectedCandidatePools.Select(poolInfo => GetCardPoolKey(poolInfo.Pool))));
        }
        catch (Exception ex)
        {
            _detectedCandidatePools = Array.Empty<CandidatePoolInfo>();
            _candidatePoolDetectionError = ex.Message;
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to detect candidate card pools: {ex}");
        }

        if (_candidatePoolControls != null && GodotObject.IsInstanceValid(_candidatePoolControls))
        {
            PopulateCandidatePoolControls(_candidatePoolControls, host);
        }
    }

    public static double? DrawingTimeLimitSeconds
    {
        get
        {
            try
            {
                int preset = NormalizeDrawingTimeLimitPreset(DrawingTimeLimitPresetBinding.Read());
                return preset switch
                {
                    0 => null,
                    -1 => Math.Clamp(CustomDrawingTimeLimitSecondsBinding.Read(), 1, 600),
                    _ => preset
                };
            }
            catch
            {
                return null;
            }
        }
    }

    private static Control BuildCandidatePoolControls(IModSettingsUiActionHost host)
    {
        VBoxContainer container = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _candidatePoolControls = container;
        PopulateCandidatePoolControls(container, host);
        return container;
    }

    private static void PopulateCandidatePoolControls(VBoxContainer container, IModSettingsUiActionHost host)
    {
        foreach (Node child in container.GetChildren())
        {
            container.RemoveChild(child);
            child.QueueFree();
        }

        try
        {
            if (_detectedCandidatePools == null)
            {
                container.AddChild(new Label
                {
                    Text = Localized(
                        "点击“检测已加载的卡池”后，这里会显示当前游戏中的所有候选卡池。",
                        "Click \"Detect Loaded Card Pools\" to show all candidate card pools currently available in the game."),
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                });
                return;
            }

            IReadOnlyList<CandidatePoolInfo> pools = _detectedCandidatePools;
            if (pools.Count == 0)
            {
                container.AddChild(new Label
                {
                    Text = string.IsNullOrWhiteSpace(_candidatePoolDetectionError)
                        ? Localized("没有检测到候选卡池。", "No candidate card pools were detected.")
                        : Localized(
                            "检测卡池失败：" + _candidatePoolDetectionError,
                            "Card-pool detection failed: " + _candidatePoolDetectionError),
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                });
                return;
            }

            container.AddChild(new Label
            {
                Text = Localized(
                    $"已检测到 {pools.Count} 个候选卡池。",
                    $"{pools.Count} candidate card pools detected."),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            });

            foreach (CandidatePoolInfo poolInfo in pools)
            {
                IModSettingsValueBinding<bool> binding = CreateCandidatePoolBinding(GetCardPoolKey(poolInfo.Pool));
                CheckButton toggle = new()
                {
                    Text = GetCardPoolDisplayName(poolInfo),
                    ButtonPressed = binding.Read(),
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                toggle.Toggled += enabled =>
                {
                    try
                    {
                        binding.Write(enabled);
                        host.MarkDirty(binding);
                    }
                    catch (Exception ex)
                    {
                        Entry.Logger.Warn($"[DrawAndGuessMod] Failed to update candidate pool {GetCardPoolKey(poolInfo.Pool)}: {ex.Message}");
                    }
                };
                container.AddChild(toggle);
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to build candidate card-pool controls: {ex.Message}");
            container.AddChild(new Label
            {
                Text = Localized(
                    "候选卡池尚未准备完成，请稍后重新打开设置。",
                    "Candidate card pools are not ready yet. Please reopen settings later."),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            });
        }
        finally
        {
            RefreshCandidatePoolLayout(container);
        }
    }

    private static void RefreshCandidatePoolLayout(VBoxContainer container)
    {
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(container))
            {
                return;
            }

            container.UpdateMinimumSize();
            container.QueueSort();

            Control? ancestor = container.GetParent() as Control;
            while (ancestor != null)
            {
                ancestor.UpdateMinimumSize();
                if (ancestor is Container parentContainer)
                {
                    parentContainer.QueueSort();
                }

                if (ancestor is ScrollContainer)
                {
                    break;
                }

                ancestor = ancestor.GetParent() as Control;
            }
        }).CallDeferred();
    }

    private static bool IsCardPoolEnabled(CardPoolModel pool)
    {
        SettingsData? settings = RitsuLibFramework
            .GetDataStore(Entry.ModId)
            .Get<SettingsData>(DataKey);
        return settings?.DisabledCardPools?.Contains(GetCardPoolKey(pool), StringComparer.Ordinal) != true;
    }

    private static List<CandidatePoolInfo> GetCandidateCardPools()
    {
        List<CandidatePoolInfo> result = new();
        foreach (CardPoolModel pool in ModelDb.All
                     .OfType<CardPoolModel>()
                      .Concat(ModelDb.AllCardPools)
                      .GroupBy(GetCardPoolKey, StringComparer.Ordinal)
                      .Select(group => group.First())
                      .Where(pool => !IsMockModel(pool) && !IsUnsupportedCandidatePool(pool)))
        {
            try
            {
                int cardCount = pool.AllCards
                    .Where(IsEligibleCandidateCard)
                    .Select(card => card.Id)
                    .Distinct()
                    .Count();
                result.Add(new CandidatePoolInfo(pool, cardCount));
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn($"[DrawAndGuessMod] Failed to inspect candidate pool {GetCardPoolKey(pool)}: {ex.Message}");
            }
        }

        return result
            .OrderBy(poolInfo => GetCardPoolDisplayName(poolInfo), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(poolInfo => GetCardPoolKey(poolInfo.Pool), StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsMockModel(AbstractModel model)
    {
        Type type = model.GetType();
        string id = model.Id.ToString();
        string entry = model.Id.Entry;
        string name = type.Name;
        string fullName = type.FullName ?? string.Empty;
        return id.Contains("mock", StringComparison.OrdinalIgnoreCase)
               || entry.Contains("mock", StringComparison.OrdinalIgnoreCase)
               || name.Contains("mock", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains(".Mocks.", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains(".Mock.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnsupportedCandidatePool(CardPoolModel pool)
    {
        // DeprivedCardPool intentionally throws from AllCards and is never a real
        // candidate source for this mod's card selection UI.
        return string.Equals(pool.Id.Entry, "DEPRIVED_CARD_POOL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEligibleCandidateCard(CardModel card)
    {
        return card is not Blank
               && card.ShouldShowInCardLibrary
               && card.Type != CardType.None;
    }

    private static string GetCardPoolKey(CardPoolModel pool)
    {
        return pool.Id.ToString();
    }

    private static int NormalizeDrawingTimeLimitPreset(int value)
    {
        return value is -1 or 0 or 15 or 30 or 60 or 90 or 120
            ? value
            : 0;
    }

    private static string GetCardPoolDisplayName(CandidatePoolInfo poolInfo)
    {
        string? characterName = ModelDb.All
            .OfType<CharacterModel>()
            .Concat(ModelDb.AllCharacters)
            .GroupBy(character => character.Id)
            .Select(group => group.First())
            .FirstOrDefault(character => character.CardPool.Id == poolInfo.Pool.Id)
            ?.Title
            .GetFormattedText();
        string poolName = string.IsNullOrWhiteSpace(characterName)
            ? GetSharedOrModdedPoolName(poolInfo.Pool)
            : characterName;
        return $"{poolName} ({poolInfo.CardCount})";
    }

    private static string GetSharedOrModdedPoolName(CardPoolModel pool)
    {
        string rawName = string.IsNullOrWhiteSpace(pool.Title)
            ? pool.Id.Entry
            : pool.Title;
        return pool.GetType().Name switch
        {
            "ColorlessCardPool" => Localized("无色", "Colorless"),
            "CurseCardPool" => Localized("诅咒", "Curse"),
            "DeprecatedCardPool" => Localized("已弃用", "Deprecated"),
            "EventCardPool" => Localized("事件", "Event"),
            "QuestCardPool" => Localized("任务", "Quest"),
            "StatusCardPool" => Localized("状态", "Status"),
            "TokenCardPool" => Localized("衍生", "Token"),
            _ => rawName
        };
    }

    internal static void StartPretrainingFromRunSettings()
    {
        StartPretrainingCore(null);
    }

    private static void StartPretraining(IModSettingsUiActionHost host)
    {
        StartPretrainingCore(host);
    }

    private static void StartPretrainingCore(IModSettingsUiActionHost? host)
    {
        if (_pretraining)
        {
            return;
        }

        _pretraining = true;
        _pretrainingProgress = 0d;
        _hasPretrainingThumbnail = false;
        SetPretrainingStatus(
            "正在分析当前所有已注册卡牌的图片，请稍候……",
            "Analyzing all currently registered card images. Please wait...");
        UpdateProgressControls();
        host?.RequestRefresh();
        Callable.From(() =>
        {
            _ = RunPretrainingAsync(host);
        }).CallDeferred();
    }

    private static async Task RunPretrainingAsync(IModSettingsUiActionHost? host)
    {
        try
        {
            CardPretrainingResult result = await CardArtClassifier.PretrainCurrentCardsAsync(UpdatePretrainingProgress);
            _pretrainingProgress = 100d;
            SetPretrainingStatus(
                $"扫描完成：共处理 {result.TotalCards} 张卡牌，跳过 {result.SkippedCards} 张；识别缓存已更新。",
                $"Scan complete: processed {result.TotalCards} cards and skipped {result.SkippedCards}. The recognition cache has been updated.");
            UpdateProgressControls();
        }
        catch (Exception ex)
        {
            SetPretrainingStatus("扫描失败：" + ex.Message, "Scan failed: " + ex.Message);
            Entry.Logger.Error($"[DrawAndGuessMod] Card-art cache scan failed: {ex}");
            UpdateProgressControls();
        }
        finally
        {
            _pretraining = false;
            UpdateProgressControls();
            try
            {
                host?.RequestRefresh();
            }
            catch
            {
            }
        }
    }

    private static Control BuildPretrainingProgressControl(IModSettingsUiActionHost host)
    {
        HBoxContainer container = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(420f, 100f)
        };
        container.AddThemeConstantOverride("separation", 12);
        _pretrainingThumbnailRect = new TextureRect
        {
            Texture = CurrentPretrainingThumbnail,
            Visible = _pretraining && CurrentPretrainingThumbnail != null,
            CustomMinimumSize = new Vector2(144f, 100f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        container.AddChild(_pretrainingThumbnailRect);

        VBoxContainer progressContainer = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _pretrainingProgressBar = new ProgressBar
        {
            MinValue = 0d,
            MaxValue = 100d,
            Value = _pretrainingProgress,
            ShowPercentage = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(240f, 24f)
        };
        _pretrainingProgressLabel = new Label
        {
            Text = PretrainingStatus,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        progressContainer.AddChild(_pretrainingProgressBar);
        progressContainer.AddChild(_pretrainingProgressLabel);
        container.AddChild(progressContainer);
        return container;
    }

    private static void UpdatePretrainingProgress(CardPretrainingProgress progress)
    {
        _pretrainingProgress = progress.TotalCards == 0
            ? 100d
            : progress.ProcessedCards * 100d / progress.TotalCards;
        SetPretrainingStatus(
            $"正在分析 {progress.ProcessedCards} / {progress.TotalCards}：{progress.CurrentCardId}",
            $"Analyzing {progress.ProcessedCards} / {progress.TotalCards}: {progress.CurrentCardId}");
        UpdatePretrainingThumbnail(progress.Thumbnail);
        UpdateProgressControls();
    }

    private static void UpdatePretrainingThumbnail(Image? thumbnail)
    {
        if (thumbnail == null || thumbnail.IsEmpty())
        {
            _hasPretrainingThumbnail = false;
            return;
        }

        _pretrainingThumbnailTextureIndex =
            (_pretrainingThumbnailTextureIndex + 1) % PretrainingThumbnailTextures.Length;
        ImageTexture? previousTexture =
            PretrainingThumbnailTextures[_pretrainingThumbnailTextureIndex];
        Image? previousUploadSource =
            PretrainingThumbnailUploadSources[_pretrainingThumbnailTextureIndex];
        Image uploadSource = Image.CreateFromData(
            thumbnail.GetWidth(),
            thumbnail.GetHeight(),
            thumbnail.HasMipmaps(),
            thumbnail.GetFormat(),
            thumbnail.GetData());
        ImageTexture texture = ImageTexture.CreateFromImage(uploadSource);
        PretrainingThumbnailUploadSources[_pretrainingThumbnailTextureIndex] = uploadSource;
        PretrainingThumbnailTextures[_pretrainingThumbnailTextureIndex] = texture;
        previousTexture?.Dispose();
        previousUploadSource?.Dispose();
        _hasPretrainingThumbnail = true;
    }

    private static void UpdateProgressControls()
    {
        if (_pretrainingProgressBar != null && GodotObject.IsInstanceValid(_pretrainingProgressBar))
        {
            _pretrainingProgressBar.Value = _pretrainingProgress;
        }
        if (_pretrainingProgressLabel != null && GodotObject.IsInstanceValid(_pretrainingProgressLabel))
        {
            _pretrainingProgressLabel.Text = PretrainingStatus;
        }
        if (_pretrainingThumbnailRect != null && GodotObject.IsInstanceValid(_pretrainingThumbnailRect))
        {
            _pretrainingThumbnailRect.Texture = CurrentPretrainingThumbnail;
            _pretrainingThumbnailRect.Visible = _pretraining && CurrentPretrainingThumbnail != null;
        }
        PretrainingChanged?.Invoke();
    }

    private static ModSettingsText LocalizedText(string simplifiedChinese, string english)
    {
        return ModSettingsText.DynamicFullRefreshOnly(() => Localized(simplifiedChinese, english));
    }

    private static ModSettingsText DynamicText(Func<string> resolver)
    {
        return ModSettingsText.DynamicFullRefreshOnly(resolver);
    }

    private static void SetPretrainingStatus(string simplifiedChinese, string english)
    {
        _pretrainingStatusChinese = simplifiedChinese;
        _pretrainingStatusEnglish = english;
    }

    private static string Localized(string simplifiedChinese, string english)
    {
        return ModText.Get(simplifiedChinese, english);
    }

    private static bool IsCurrentRunMultiplayer()
    {
        return RunManager.Instance?.DebugOnlyGetState()?.Players.Count > 1;
    }

    private sealed class SettingsData
    {
        public int CardPoolScope { get; set; } = (int)GuessCardPoolScope.AllCards;
        public bool IncludeMultiplayerCards { get; set; } = true;
        public bool ExcludePreviouslySelectedBlankCards { get; set; }
        public bool BlankGeneratedCardSkipsDeck { get; set; }
        public bool TracingEnabled { get; set; }
        public bool TreasureRoomRelicDrawingEnabled { get; set; } = true;
        public bool GainBlankAtRunStart { get; set; }
        public int RecognitionModelAccuracy { get; set; } = (int)DrawAndGuessMod.Scripts.Config.RecognitionModelAccuracy.Jibao;
        public int DrawingTimeLimitPreset { get; set; }
        public int CustomDrawingTimeLimitSeconds { get; set; } = 60;
        public List<string> DisabledCardPools { get; set; } = new();
    }

    private sealed record CandidatePoolInfo(CardPoolModel Pool, int CardCount);
}
