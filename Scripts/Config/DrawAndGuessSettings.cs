using System;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Ai;
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
    private static string _pretrainingStatus = "提前提取当前已安装的原版及模组卡牌特征，并保存为本地缓存。";
    private static double _pretrainingProgress;
    private static ProgressBar? _pretrainingProgressBar;
    private static Label? _pretrainingProgressLabel;

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
        page
            .WithModDisplayName(Text("你画我猜"))
            .WithTitle(Text("你画我猜"))
            .WithDescription(Text("设置空白卡牌的 AI 猜测候选范围。"))
            .WithVisibleOnHostSurfaces(
                ModSettingsHostSurface.MainMenu |
                ModSettingsHostSurface.RunPause |
                ModSettingsHostSurface.CombatPause)
            .AddSection("guess_pool", section => section
                .WithTitle(Text("AI 候选牌池"))
                .AddChoice(
                    "card_pool_scope",
                    Text("识别范围"),
                    CardPoolScopeBinding,
                    new[]
                    {
                        new ModSettingsChoiceOption<int>((int)GuessCardPoolScope.AllCards, Text("全卡池")),
                        new ModSettingsChoiceOption<int>((int)GuessCardPoolScope.CurrentCharacter, Text("当前角色卡池"))
                    },
                    Text("全卡池：从所有可见卡牌中猜测。当前角色卡池：只从当前角色自己的卡牌中猜测。"),
                    ModSettingsChoicePresentation.Dropdown)
                .AddToggle(
                    "include_multiplayer_cards",
                    Text("启用多人卡牌"),
                    IncludeMultiplayerCardsBinding,
                    Text("开启后，AI 候选中可以包含仅限多人模式的卡牌；关闭时会排除这些牌。"),
                    () => true)
                .AddChoice(
                    "recognition_model_accuracy",
                    Text("识别模型准确度"),
                    RecognitionModelAccuracyBinding,
                    new[]
                    {
                        new ModSettingsChoiceOption<int>((int)RecognitionModelAccuracy.Waku, Text("瓦库")),
                        new ModSettingsChoiceOption<int>((int)RecognitionModelAccuracy.Jibao, Text("鸡煲"))
                    },
                    Text("瓦库：特征提取算法。鸡煲：DINOv2模型"),
                    ModSettingsChoicePresentation.Dropdown))
            .AddSection("model_training", section => section
                .WithTitle(Text("本地识别模型"))
                .AddCustom(
                    "pretraining_progress",
                    Text("预训练进度"),
                    BuildPretrainingProgressControl,
                    Text("显示当前特征提取进度。"),
                    () => _pretraining)
                .AddButton(
                    "pretrain_current_cards",
                    Text("特征缓存"),
                    Text(_pretraining ? "正在预训练……" : "预训练当前所有卡牌"),
                    StartPretraining,
                    ModSettingsButtonTone.Accent,
                    Text(_pretrainingStatus))
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
        _pretrainingStatus = "正在读取所有已注册卡牌的立绘并提取特征，请稍候……";
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
            _pretrainingStatus = $"完成：共 {result.TotalCards} 张；手工特征新提取 {result.ExtractedCards}、复用 {result.ReusedCards}；DINOv2 新提取 {result.DinoExtractedCards}、复用 {result.DinoReusedCards}；跳过 {result.SkippedCards} 张。";
            UpdateProgressControls();
        }
        catch (Exception ex)
        {
            _pretrainingStatus = "预训练失败：" + ex.Message;
            Entry.Logger.Error($"[DrawAndGuessMod] Manual pretraining failed: {ex}");
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
            Text = _pretrainingStatus,
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
        _pretrainingStatus = $"正在处理 {progress.ProcessedCards} / {progress.TotalCards}：{progress.CurrentCardId}";
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
            _pretrainingProgressLabel.Text = _pretrainingStatus;
        }
    }

    private static ModSettingsText Text(string value)
    {
        return ModSettingsText.Literal(value);
    }

    private sealed class SettingsData
    {
        public int CardPoolScope { get; set; } = (int)GuessCardPoolScope.AllCards;
        public bool IncludeMultiplayerCards { get; set; } = true;
        public int RecognitionModelAccuracy { get; set; } = (int)DrawAndGuessMod.Scripts.Config.RecognitionModelAccuracy.Jibao;
    }
}
