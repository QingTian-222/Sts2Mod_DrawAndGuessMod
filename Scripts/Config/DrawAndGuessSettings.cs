using System;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Ai;
using DrawAndGuessMod.Scripts.Localization;
using Godot;
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
    Jibao = 1
}

internal static class DrawAndGuessSettings
{
    private const string DataKey = "settings";
    private const string FileName = "settings.json";
    private static bool _pretraining;
    private static string _pretrainingStatusChinese = string.Empty;
    private static string _pretrainingStatusEnglish = string.Empty;
    private static double _pretrainingProgress;
    private static ProgressBar? _pretrainingProgressBar;
    private static Label? _pretrainingProgressLabel;
    private static string PretrainingStatus => Localized(_pretrainingStatusChinese, _pretrainingStatusEnglish);

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

    private static readonly IModSettingsValueBinding<bool> BlankGeneratedCardSkipsDeckBinding =
        ModSettingsBindings.Global<SettingsData, bool>(
            Entry.ModId,
            DataKey,
            data => data.BlankGeneratedCardSkipsDeck,
            (data, value) => data.BlankGeneratedCardSkipsDeck = value);

    private static readonly IModSettingsValueBinding<bool> GainBlankAtRunStartBinding =
        ModSettingsBindings.Global<SettingsData, bool>(
            Entry.ModId,
            DataKey,
            data => data.GainBlankAtRunStart,
            (data, value) => data.GainBlankAtRunStart = value);

    private static readonly IModSettingsValueBinding<int> RecognitionModelAccuracyBinding =
        ModSettingsBindings.Global<SettingsData, int>(
            Entry.ModId,
            DataKey,
            data => data.RecognitionModelAccuracy,
            (data, value) => data.RecognitionModelAccuracy = Math.Clamp(value, 0, 1));

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

    public static bool GainBlankAtRunStart
    {
        get
        {
            try
            {
                return GainBlankAtRunStartBinding.Read();
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
                return (RecognitionModelAccuracy)Math.Clamp(RecognitionModelAccuracyBinding.Read(), 0, 1);
            }
            catch
            {
                return RecognitionModelAccuracy.Jibao;
            }
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
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to register settings: {ex.Message}");
            GD.PushWarning($"[DrawAndGuessMod] Failed to register settings: {ex.Message}");
        }
    }

    private static void BuildSettingsPage(ModSettingsPageBuilder page)
    {
        if (string.IsNullOrEmpty(_pretrainingStatusChinese))
        {
            SetPretrainingStatus(
                "读取当前安装的原版及模组卡牌图片并建立识别缓存，使新卡牌能够参与猜测，并减少游戏中的等待时间。",
                "Scan images from the base game and installed card mods to build a recognition cache. This lets new cards participate in guesses and reduces in-game waiting.");
        }

        page
            .WithModDisplayName(LocalizedText("你画瓦猜", "Draw & Guess"))
            .WithTitle(LocalizedText("你画瓦猜", "Draw & Guess"))
            .WithDescription(LocalizedText(
                "设置空白卡牌的 AI 猜测候选范围。",
                "Configure the AI candidate pool used by Blank."))
            .WithVisibleOnHostSurfaces(
                ModSettingsHostSurface.MainMenu |
                ModSettingsHostSurface.RunPause |
                ModSettingsHostSurface.CombatPause)
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
                .AddToggle(
                    "gain_blank_at_run_start",
                    LocalizedText(
                        "对局开始时获得一张空白",
                        "Gain a Blank at Run Start"),
                    GainBlankAtRunStartBinding,
                    LocalizedText(
                        "开启后，新一局对局开始时会将一张“空白”加入你的初始牌组。多人模式下每位玩家分别使用自己的设置。默认关闭。",
                        "When enabled, one Blank is added to your starting Deck at the beginning of a new run. In multiplayer, each player uses their own setting. Disabled by default."),
                    () => true)
                .AddChoice(
                    "recognition_model_accuracy",
                    LocalizedText("识别模型准确度", "Recognition Model"),
                    RecognitionModelAccuracyBinding,
                    new[]
                    {
                        new ModSettingsChoiceOption<int>((int)RecognitionModelAccuracy.Waku, LocalizedText("瓦库", "VAKUU")),
                        new ModSettingsChoiceOption<int>((int)RecognitionModelAccuracy.Jibao, LocalizedText("鸡煲", "Defect"))
                    },
                    LocalizedText(
                        "瓦库：手工视觉特征。鸡煲：手工特征与 DINOv2 各占 50%。",
                        "VAKUU: handcrafted visual features. Defect: a 50/50 fusion of handcrafted features and DINOv2."),
                    ModSettingsChoicePresentation.Dropdown))
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
                .WithEntryEnabledWhen("pretrain_current_cards", () => !_pretraining));
    }

    private static void StartPretraining(IModSettingsUiActionHost host)
    {
        if (_pretraining)
        {
            return;
        }

        _pretraining = true;
        _pretrainingProgress = 0d;
        SetPretrainingStatus(
            "正在分析当前所有已注册卡牌的图片，请稍候……",
            "Analyzing all currently registered card images. Please wait...");
        host.RequestRefresh();
        Callable.From(() =>
        {
            _ = RunPretrainingAsync(host);
        }).CallDeferred();
    }

    private static async Task RunPretrainingAsync(IModSettingsUiActionHost host)
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
        }
        finally
        {
            _pretraining = false;
            try
            {
                host.RequestRefresh();
            }
            catch
            {
            }
        }
    }

    private static Control BuildPretrainingProgressControl(IModSettingsUiActionHost host)
    {
        VBoxContainer container = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(420f, 58f)
        };
        _pretrainingProgressBar = new ProgressBar
        {
            MinValue = 0d,
            MaxValue = 100d,
            Value = _pretrainingProgress,
            ShowPercentage = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(400f, 24f)
        };
        _pretrainingProgressLabel = new Label
        {
            Text = PretrainingStatus,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        container.AddChild(_pretrainingProgressBar);
        container.AddChild(_pretrainingProgressLabel);
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
        UpdateProgressControls();
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

    private sealed class SettingsData
    {
        public int CardPoolScope { get; set; } = (int)GuessCardPoolScope.AllCards;
        public bool IncludeMultiplayerCards { get; set; } = true;
        public bool BlankGeneratedCardSkipsDeck { get; set; }
        public bool GainBlankAtRunStart { get; set; }
        public int RecognitionModelAccuracy { get; set; } = (int)DrawAndGuessMod.Scripts.Config.RecognitionModelAccuracy.Jibao;
    }
}
