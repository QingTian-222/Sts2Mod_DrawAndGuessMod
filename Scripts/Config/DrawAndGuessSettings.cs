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
    private static string _pretrainingStatusKey = string.Empty;
    private static (string Name, object Value)[] _pretrainingStatusVariables = [];
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
    private static string PretrainingStatus => string.IsNullOrEmpty(_pretrainingStatusKey)
        ? string.Empty
        : ModText.Format(_pretrainingStatusKey, _pretrainingStatusVariables);

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
            .WithModDisplayName(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.DRAW_GUESS"))
            .WithTitle(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.DRAW_GUESS"))
            .WithSortOrder(0)
            .WithDescription(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.CHOOSE_A_CATEGORY_TO_VIEW_ITS_SETTINGS"))
            .WithVisibleOnHostSurfaces(SettingsHostSurfaces)
            .AddSection("settings_categories", section => section
                .WithTitle(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.SETTINGS_CATEGORIES"))
                .AddSubpage(
                    "open_ai_settings",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.AI_SETTINGS"),
                    AiSettingsPageId,
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.OPEN"),
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.RECOGNITION_CACHE_SCOPE_MULTIPLAYER_CARDS_MODEL_SELECTION"))
                .AddSubpage(
                    "open_game_settings",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.GAME_SETTINGS"),
                    GameSettingsPageId,
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.OPEN"),
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.CONFIGURE_BLANK_DRAWING_TIME_LIMITS_REFERENCE_IMAGES"))
                .AddSubpage(
                    "open_artwork_history",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.ARTWORK_HISTORY"),
                    ArtworkHistoryPageId,
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.OPEN"),
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.BROWSE_ALL_COMPLETED_CARD_AND_RELIC_DRAWINGS")));
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
        if (string.IsNullOrEmpty(_pretrainingStatusKey))
        {
            SetPretrainingStatus("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.SCAN_IMAGES_FROM_THE_BASE_GAME_AND_INSTALLED");
        }

        page
            .AsChildOf(Entry.ModId)
            .WithTitle(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.AI_SETTINGS"))
            .WithSortOrder(10)
            .WithDescription(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.MANAGE_RECOGNITION_MODELS_CACHES_AND_AI_CANDIDATE"))
            .WithVisibleOnHostSurfaces(SettingsHostSurfaces)
            .AddSection("model_training", section => section
                .WithTitle(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.CARD_RECOGNITION_CACHE"))
                .AddCustom(
                    "pretraining_progress",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.CARD_SCAN_PROGRESS"),
                    BuildPretrainingProgressControl,
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.SHOWS_PROGRESS_WHILE_CARD_IMAGES_ARE_BEING"),
                    () => _pretraining)
                .AddButton(
                    "pretrain_current_cards",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.RECOGNITION_CACHE"),
                    DynamicText(() => _pretraining
                        ? Localized("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.ANALYZING_CARD_IMAGES")
                        : Localized("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.SCAN_CARDS_AND_BUILD_CACHE")),
                    StartPretraining,
                    ModSettingsButtonTone.Accent,
                    DynamicText(() => PretrainingStatus))
                .WithEntryEnabledWhen("pretrain_current_cards", () => !_pretraining))
            .AddSection("guess_pool", section => section
                .WithTitle(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.AI_CANDIDATE_POOL"))
                .AddChoice(
                    "card_pool_scope",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.RECOGNITION_SCOPE"),
                    CardPoolScopeBinding,
                    new[]
                    {
                        new ModSettingsChoiceOption<int>((int)GuessCardPoolScope.AllCards, LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.ALL_CARDS")),
                        new ModSettingsChoiceOption<int>((int)GuessCardPoolScope.CurrentCharacter, LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.CURRENT_CHARACTER"))
                    },
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.ALL_CARDS_GUESS_FROM_EVERY_VISIBLE_CARD"),
                    ModSettingsChoicePresentation.Dropdown)
                .AddToggle(
                    "include_multiplayer_cards",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.INCLUDE_MULTIPLAYER_CARDS"),
                    IncludeMultiplayerCardsBinding,
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.ALLOW_MULTIPLAYER_ONLY_CARDS_TO_APPEAR_AMONG"),
                    () => true)
                .AddChoice(
                    "recognition_model_accuracy",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.RECOGNITION_MODEL"),
                    RecognitionModelAccuracyBinding,
                    new[]
                    {
                        new ModSettingsChoiceOption<int>((int)RecognitionModelAccuracy.Waku, LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.VAKUU")),
                        new ModSettingsChoiceOption<int>((int)RecognitionModelAccuracy.Jibao, LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.DEFECT")),
                        new ModSettingsChoiceOption<int>(
                            (int)RecognitionModelAccuracy.SketchAdapter,
                            LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.TRAINED_ADAPTER_EXPERIMENTAL"))
                    },
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.VAKUU_BASIC_MODEL_DEFECT_BETTER_NN_MODEL"),
                    ModSettingsChoicePresentation.Dropdown))
            .AddSection("advanced_candidate_pools", section => section
                .WithTitle(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.ADVANCED_OPTIONS"))
                .WithDescription(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.DISABLE_INDIVIDUAL_CARD_POOLS_THAT_SHOULD_NOT"))
                .Collapsible(true)
                .AddButton(
                    "detect_candidate_card_pools",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.DETECT_CARD_POOLS"),
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.DETECT_RE_DETECT_LOADED_CARD_POOLS"),
                    DetectCandidateCardPools,
                    ModSettingsButtonTone.Accent,
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.READ_CANDIDATE_CARD_POOLS_AFTER_ALL_MODS"))
                .AddCustom(
                    "candidate_card_pools",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.CANDIDATE_CARD_POOLS"),
                    BuildCandidatePoolControls,
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.DISABLED_POOLS_WILL_NOT_APPEAR_IN_RECOGNITION"),
                    () => true));
    }

    private static void BuildGameSettingsPage(ModSettingsPageBuilder page)
    {
        page
            .AsChildOf(Entry.ModId)
            .WithTitle(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.GAME_SETTINGS"))
            .WithSortOrder(20)
            .WithDescription(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.CONFIGURE_BLANK_AND_THE_DRAWING_INTERFACE"))
            .WithVisibleOnHostSurfaces(SettingsHostSurfaces)
            .AddSection("blank_rules", section => section
                .WithTitle(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.BLANK"))
                .AddToggle(
                    "exclude_previously_selected_blank_cards",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.EXCLUDE_CARDS_PREVIOUSLY_SELECTED_BY_BLANK"),
                    ExcludePreviouslySelectedBlankCardsBinding,
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.PREVIOUSLY_SELECTED_BLANK_CARDS_WILL_NO_LONGER"),
                    () => true)
                .AddToggle(
                    "blank_generated_card_skips_deck",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.DO_NOT_ADD_BLANK_S_CARD_TO"),
                    BlankGeneratedCardSkipsDeckBinding,
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.WHEN_ENABLED_THE_CARD_GENERATED_BY_BLANK"),
                    () => true)
                .AddChoice(
                    "drawing_time_limit",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.DRAWING_TIME_LIMIT"),
                    DrawingTimeLimitPresetBinding,
                    new[]
                    {
                        new ModSettingsChoiceOption<int>(0, LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.OFF")),
                        new ModSettingsChoiceOption<int>(15, LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.15_SECONDS")),
                        new ModSettingsChoiceOption<int>(30, LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.30_SECONDS")),
                        new ModSettingsChoiceOption<int>(60, LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.60_SECONDS")),
                        new ModSettingsChoiceOption<int>(90, LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.90_SECONDS")),
                        new ModSettingsChoiceOption<int>(120, LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.120_SECONDS")),
                        new ModSettingsChoiceOption<int>(-1, LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.CUSTOM"))
                    },
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.LIMIT_DRAWING_TIME_FOR_THE_REGULAR_BLANK"),
                    ModSettingsChoicePresentation.Dropdown)
                .AddIntSlider(
                    "custom_drawing_time_limit_seconds",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.CUSTOM_SECONDS"),
                    CustomDrawingTimeLimitSecondsBinding,
                    1,
                    600,
                    1,
                    value => ModText.Format(
                        "DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.SECONDS_VALUE",
                        ("Seconds", value)),
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.SET_A_CUSTOM_DRAWING_TIME_FOR_THE"))
                .WithEntryVisibleWhen(
                    "custom_drawing_time_limit_seconds",
                    () => NormalizeDrawingTimeLimitPreset(DrawingTimeLimitPresetBinding.Read()) == -1))
            .AddSection("drawing_ui", section => section
                .WithTitle(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.DRAWING_INTERFACE"))
                .AddToggle(
                    "treasure_room_relic_drawing_enabled",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.DRAW_RELICS_IN_TREASURE_ROOMS"),
                    TreasureRoomRelicDrawingEnabledBinding,
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.IN_MULTIPLAYER_EACH_PLAYER_DRAWS_A_RELIC"),
                    () => true)
                .WithEntryVisibleWhen(
                    "treasure_room_relic_drawing_enabled",
                    IsCurrentRunMultiplayer)
                .AddToggle(
                    "tracing_enabled",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.REFERENCE_IMAGE"),
                    TracingEnabledBinding,
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.REPLACE_SWITCH_CANVAS_WITH_A_REFERENCE_BUTTON"),
                    () => true));
    }

    private static void BuildArtworkHistoryPage(ModSettingsPageBuilder page)
    {
        page
            .AsChildOf(Entry.ModId)
            .WithTitle(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.ARTWORK_HISTORY"))
            .WithSortOrder(30)
            .WithDescription(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.BROWSE_COMPLETED_BLANK_GALLERY_CHALLENGE_AND_RELIC"))
            .WithVisibleOnHostSurfaces(SettingsHostSurfaces)
            .AddSection("artworks", section => section
                .WithTitle(LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.ARTWORKS"))
                .AddCustom(
                    "artwork_entries",
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.HISTORY"),
                    ArtworkHistoryViewer.BuildSettingsControl,
                    LocalizedText("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.ENTRIES_ARE_ORDERED_NEWEST_FIRST_RENAME_EDIT")));
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
                    Text = Localized("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.CLICK_DETECT_LOADED_CARD_POOLS_TO_SHOW"),
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
                        ? Localized("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.NO_CANDIDATE_CARD_POOLS_WERE_DETECTED")
                        : ModText.Format(
                            "DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.CARD_POOL_DETECTION_FAILED",
                            ("Error", _candidatePoolDetectionError)),
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                });
                return;
            }

            container.AddChild(new Label
            {
                Text = ModText.Format(
                    "DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.CANDIDATE_CARD_POOLS_DETECTED",
                    ("Count", pools.Count)),
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
                Text = Localized("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.CANDIDATE_CARD_POOLS_ARE_NOT_READY_YET"),
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
        foreach (CardPoolModel pool in DrawAndGuessMod.Scripts.Compatibility.ModelDbCompat
                     .GetAll<CardPoolModel>()
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
        string? characterName = DrawAndGuessMod.Scripts.Compatibility.ModelDbCompat
            .GetAll<CharacterModel>()
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
            "ColorlessCardPool" => Localized("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.COLORLESS"),
            "CurseCardPool" => Localized("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.CURSE"),
            "DeprecatedCardPool" => Localized("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.DEPRECATED"),
            "EventCardPool" => Localized("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.EVENT"),
            "QuestCardPool" => Localized("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.QUEST"),
            "StatusCardPool" => Localized("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.STATUS"),
            "TokenCardPool" => Localized("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.TOKEN"),
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
        SetPretrainingStatus("DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.ANALYZING_ALL_CURRENTLY_REGISTERED_CARD_IMAGES");
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
                "DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.SCAN_COMPLETE",
                ("TotalCards", result.TotalCards),
                ("SkippedCards", result.SkippedCards));
            UpdateProgressControls();
        }
        catch (Exception ex)
        {
            SetPretrainingStatus(
                "DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.SCAN_FAILED",
                ("Error", ex.Message));
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
            "DRAW_AND_GUESS_MOD.DRAW_AND_GUESS_SETTINGS.ANALYZING_PROGRESS",
            ("ProcessedCards", progress.ProcessedCards),
            ("TotalCards", progress.TotalCards),
            ("CurrentCardId", progress.CurrentCardId));
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

    private static ModSettingsText LocalizedText(string key)
    {
        return ModSettingsText.DynamicFullRefreshOnly(() => Localized(key));
    }

    private static ModSettingsText DynamicText(Func<string> resolver)
    {
        return ModSettingsText.DynamicFullRefreshOnly(resolver);
    }

    private static void SetPretrainingStatus(string key, params (string Name, object Value)[] variables)
    {
        _pretrainingStatusKey = key;
        _pretrainingStatusVariables = variables;
    }

    private static string Localized(string key)
    {
        return ModText.Get(key);
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
