using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Cards;
using DrawAndGuessMod.Scripts.Config;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.State;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Ai;

public sealed record CardGuess(
    CardModel Card,
    int SelectedRank,
    double Distance,
    IReadOnlyList<CardModel> NearestCards);
public sealed record CardPretrainingResult(
    int TotalCards,
    int ExtractedCards,
    int ReusedCards,
    int DinoExtractedCards,
    int DinoReusedCards,
    int SkippedCards,
    string ModelPath);
public sealed record CardPretrainingProgress(
    int ProcessedCards,
    int TotalCards,
    string CurrentCardId,
    Image? Thumbnail);
public enum GuessCandidateScope
{
    Default = 0,
    CurrentCharacterAndShared = 1,
    PartyCharactersAndShared = 2
}

internal static class CardArtClassifier
{
    private const int GridSize = 8;
    private const int ColorGridSize = 4;
    private const int FineGridSize = 16;
    private const int SampleSize = 32;
    private const int EdgeFeatureCount = GridSize * GridSize + 6;
    private const int FineEdgeFeatureCount = FineGridSize * FineGridSize;
    private const int ColorFeatureCount = ColorGridSize * ColorGridSize * 3 + 10;
    private const int FeatureCount = EdgeFeatureCount + FineEdgeFeatureCount + ColorFeatureCount;
    private const int PretrainingThumbnailWidth = 144;
    private const int PretrainingThumbnailHeight = 100;
    private const int ModelVersion = 6;
    private const int DinoModelVersion = 2;
    private const double HybridHandcraftedWeight = 0.5d;
    private const double HybridDinoWeight = 0.5d;
    private const double AdapterHandcraftedWeight = 0.3d;
    private const double AdapterDinoWeight = 0.7d;
    private static readonly SemaphoreSlim PretrainingGate = new(1, 1);
    private static List<TrainingSample>? _samples;
    private static Dictionary<string, double[]>? _pretrainedFeatures;
    private static Dictionary<string, float[]>? _pretrainedDinoFeatures;
    private static Task? _asyncTrainingTask;

    public static void Preload()
    {
        if (_pretrainedFeatures != null && _pretrainedDinoFeatures != null)
        {
            return;
        }

        _pretrainedFeatures = LoadPretrainedFeatures();
        _pretrainedDinoFeatures = LoadPretrainedDinoFeatures();
        if (_pretrainedFeatures.Count > 0)
        {
            Entry.Logger.Info($"[DrawAndGuessMod] Preloaded {_pretrainedFeatures.Count} card-art feature records.");
        }
        if (_pretrainedDinoFeatures.Count > 0)
        {
            Entry.Logger.Info($"[DrawAndGuessMod] Preloaded {_pretrainedDinoFeatures.Count} DINOv2 card-art embeddings.");
        }
        DinoArtEmbedder.Preload();
        if (DrawAndGuessSettings.RecognitionModelAccuracy == RecognitionModelAccuracy.SketchAdapter)
        {
            SketchEmbeddingAdapter.Preload();
        }
    }

    public static async Task<CardPretrainingResult> PretrainCurrentCardsAsync(Action<CardPretrainingProgress>? reportProgress = null)
    {
        await PretrainingGate.WaitAsync();
        try
        {
            return await PretrainCurrentCardsCoreAsync(reportProgress);
        }
        finally
        {
            PretrainingGate.Release();
        }
    }

    private static async Task<CardPretrainingResult> PretrainCurrentCardsCoreAsync(Action<CardPretrainingProgress>? reportProgress)
    {
        Preload();
        List<CardModel> cards = GetEligibleCards();
        Dictionary<string, double[]> generatedFeatures = new(cards.Count, StringComparer.Ordinal);
        Dictionary<string, float[]> generatedDinoFeatures = new(cards.Count, StringComparer.Ordinal);
        List<TrainingSample> generatedSamples = new(cards.Count);
        int extracted = 0;
        int reused = 0;
        int dinoExtracted = 0;
        int dinoReused = 0;
        int skipped = 0;
        using PortraitGpuImageLoader imageLoader = new();

        for (int cardIndex = 0; cardIndex < cards.Count; cardIndex++)
        {
            CardModel card = cards[cardIndex];
            double[]? features = null;
            float[]? dinoFeatures = null;
            Image? thumbnail = null;
            bool freshImageAvailable = false;
            try
            {
                using Image? image = await imageLoader.LoadAsync(card.PortraitPath);
                if (image != null && !image.IsEmpty())
                {
                    freshImageAvailable = true;
                    features = ExtractFeatures(image, treatAsSketch: false);
                    extracted++;
                    if (DinoArtEmbedder.TryExtract(image, out float[] extractedDinoFeatures))
                    {
                        dinoFeatures = extractedDinoFeatures;
                        dinoExtracted++;
                    }
                    if (reportProgress != null)
                    {
                        thumbnail = CreatePretrainingThumbnail(image);
                    }
                }
            }
            catch (PortraitReadbackUnavailableException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Entry.Logger.Debug($"[DrawAndGuessMod] Failed to freshly extract {card.Id.Entry}: {ex.Message}");
            }

            if (!freshImageAvailable &&
                features == null &&
                _pretrainedFeatures != null &&
                _pretrainedFeatures.TryGetValue(card.Id.Entry, out double[]? fallback))
            {
                features = fallback;
                reused++;
            }

            if (!freshImageAvailable &&
                dinoFeatures == null &&
                _pretrainedDinoFeatures != null &&
                _pretrainedDinoFeatures.TryGetValue(card.Id.Entry, out float[]? dinoFallback))
            {
                dinoFeatures = dinoFallback;
                dinoReused++;
            }

            if (features == null)
            {
                skipped++;
            }
            else
            {
                generatedFeatures[card.Id.Entry] = features;
                if (dinoFeatures != null)
                {
                    generatedDinoFeatures[card.Id.Entry] = dinoFeatures;
                }
                generatedSamples.Add(new TrainingSample(card, features, dinoFeatures));
            }

            try
            {
                reportProgress?.Invoke(new CardPretrainingProgress(
                    cardIndex + 1,
                    cards.Count,
                    card.Id.Entry,
                    thumbnail));
            }
            finally
            {
                thumbnail?.Dispose();
            }
        }

        if (generatedFeatures.Count == 0)
        {
            throw new InvalidOperationException(ModText.Get(
                "当前没有可用于建立识别缓存的卡牌图片。",
                "No card images are available for building the recognition cache."));
        }

        string modelPath = GetUserModelPath();
        SavePretrainedFeatures(modelPath, generatedFeatures);
        if (generatedDinoFeatures.Count > 0)
        {
            SavePretrainedDinoFeatures(GetUserDinoModelPath(), generatedDinoFeatures);
        }
        _pretrainedFeatures = generatedFeatures;
        _pretrainedDinoFeatures = generatedDinoFeatures;
        _samples = generatedSamples;
        Entry.Logger.Info($"[DrawAndGuessMod] Pretrained current card pool: total={cards.Count}, handcrafted={extracted}+{reused}, dino={dinoExtracted}+{dinoReused}, skipped={skipped}, path={modelPath}");
        return new CardPretrainingResult(cards.Count, extracted, reused, dinoExtracted, dinoReused, skipped, modelPath);
    }

    public static CardGuess Guess(Image drawing, Player owner, GuessCandidateScope candidateScope = GuessCandidateScope.Default, IReadOnlySet<ModelId>? excludedCardIds = null)
    {
        EnsureTrained();
        IEnumerable<TrainingSample> filteredCandidates = FilterCandidates(owner, candidateScope);
        if (excludedCardIds is { Count: > 0 })
        {
            filteredCandidates = filteredCandidates.Where(sample =>
                !excludedCardIds.Contains(sample.Card.Id));
        }
        List<TrainingSample> candidates = filteredCandidates.ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(ModText.Get(
                "当前设置下没有可用于识别的卡牌立绘。",
                "No card illustrations are available under the current recognition settings."));
        }

        double[] drawingFeatures = ExtractFeatures(drawing, treatAsSketch: true);
        double[] currentDistances = candidates
            .Select(sample => Distance(drawingFeatures, sample.Features))
            .ToArray();
        double[] currentScores = currentDistances.Select(distance => -distance).ToArray();
        double[] standardizedCurrentScores = Standardize(currentScores);

        RecognitionModelAccuracy recognitionModel = DrawAndGuessSettings.RecognitionModelAccuracy;
        bool useHybridModel = recognitionModel != RecognitionModelAccuracy.Waku;
        bool useSketchAdapter = recognitionModel == RecognitionModelAccuracy.SketchAdapter;
        bool adaptedDrawing = false;
        float[] drawingDinoFeatures = [];
        bool hasDinoDrawing = useHybridModel && DinoArtEmbedder.TryExtract(drawing, out drawingDinoFeatures);
        if (hasDinoDrawing &&
            useSketchAdapter &&
            SketchEmbeddingAdapter.TryAdapt(drawingDinoFeatures, out float[] adaptedFeatures))
        {
            drawingDinoFeatures = adaptedFeatures;
            adaptedDrawing = true;
        }
        double[] dinoSimilarities = new double[candidates.Count];
        bool[] hasDinoCandidate = new bool[candidates.Count];
        if (hasDinoDrawing)
        {
            for (int index = 0; index < candidates.Count; index++)
            {
                float[]? candidateFeatures = candidates[index].DinoFeatures;
                if (candidateFeatures == null)
                {
                    continue;
                }
                dinoSimilarities[index] = CosineSimilarity(drawingDinoFeatures, candidateFeatures);
                hasDinoCandidate[index] = true;
            }
        }
        bool useDino = hasDinoDrawing && hasDinoCandidate.Any(value => value);
        double[] standardizedDinoScores = useDino
            ? StandardizeAvailable(dinoSimilarities, hasDinoCandidate)
            : new double[candidates.Count];
        double handcraftedWeight = adaptedDrawing
            ? AdapterHandcraftedWeight
            : HybridHandcraftedWeight;
        double dinoWeight = adaptedDrawing
            ? AdapterDinoWeight
            : HybridDinoWeight;

        List<RankedCandidate> ranked = candidates
            .Select((sample, index) => new RankedCandidate(
                sample,
                currentDistances[index],
                standardizedCurrentScores[index],
                dinoSimilarities[index],
                standardizedDinoScores[index],
                hasDinoCandidate[index],
                useDino
                    ? handcraftedWeight * standardizedCurrentScores[index] + dinoWeight * standardizedDinoScores[index]
                    : standardizedCurrentScores[index]))
            .OrderByDescending(item => item.FusedScore)
            .ThenBy(item => item.Sample.Card.Id.Entry, StringComparer.Ordinal)
            .ToList();

        const int selectedRank = 0;
        RankedCandidate selected = ranked[selectedRank];
        int nearestCount = Math.Min(3, ranked.Count);
        IReadOnlyList<CardModel> nearest = ranked.Take(nearestCount).Select(item => item.Sample.Card).ToList();
        string diagnostics = string.Join(", ", ranked.Take(6).Select(item =>
            $"{item.Sample.Card.Id.Entry}:score={item.FusedScore:F3}, handDist={item.CurrentDistance:F3}, dino={(item.HasDino ? item.DinoSimilarity.ToString("F3") : "n/a")}"));
        string dinoMode = useDino
            ? adaptedDrawing ? "adapted" : "raw"
            : "unavailable";
        Entry.Logger.Info($"[DrawAndGuessMod] Guess candidates ({DrawingRunRules.GetCardRestriction(owner.RunState)}, multiplayer={DrawAndGuessSettings.IncludeMultiplayerCards}, excludedPreviousBlank={excludedCardIds?.Count ?? 0}, model={recognitionModel}, dino={dinoMode}): {diagnostics}");
        return new CardGuess(selected.Sample.Card, selectedRank, selected.CurrentDistance, nearest);
    }

    private static IEnumerable<TrainingSample> FilterCandidates(Player owner, GuessCandidateScope candidateScope = GuessCandidateScope.Default)
    {
        IEnumerable<TrainingSample> candidates = _samples ?? Enumerable.Empty<TrainingSample>();
        candidates = candidates.Where(sample => !ErasedCardStore.IsErased(owner.RunState, sample.Card.Id));
        HashSet<ModelId> excludedCardIds = DrawAndGuessSettings.GetCardIdsExcludedByAdvancedPoolSettings();
        if (excludedCardIds.Count > 0)
        {
            candidates = candidates.Where(sample => !excludedCardIds.Contains(sample.Card.Id));
        }

        if (!DrawAndGuessSettings.IncludeMultiplayerCards)
        {
            candidates = candidates.Where(sample => sample.Card.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly);
        }

        if (candidateScope == GuessCandidateScope.CurrentCharacterAndShared)
        {
            HashSet<ModelId> allowedCardIds = owner.Character.CardPool.AllCardIds.ToHashSet();
            foreach (CardPoolModel sharedPool in ModelDb.AllSharedCardPools)
            {
                allowedCardIds.UnionWith(sharedPool.AllCardIds);
            }
            candidates = candidates.Where(sample => allowedCardIds.Contains(sample.Card.Id));
        }
        else if (candidateScope == GuessCandidateScope.PartyCharactersAndShared)
        {
            HashSet<ModelId> allowedCardIds = owner.RunState.Players
                .SelectMany(player => player.Character.CardPool.AllCardIds)
                .ToHashSet();
            foreach (CardPoolModel sharedPool in ModelDb.AllSharedCardPools)
            {
                allowedCardIds.UnionWith(sharedPool.AllCardIds);
            }
            candidates = candidates.Where(sample => allowedCardIds.Contains(sample.Card.Id));
        }
        else
        {
            DrawingCardRestriction restriction = DrawingRunRules.GetCardRestriction(owner.RunState);
            if (restriction == DrawingCardRestriction.ExcludeAncient)
            {
                candidates = candidates.Where(sample => sample.Card.Rarity != CardRarity.Ancient);
            }
            else if (restriction == DrawingCardRestriction.CurrentCharacter)
            {
                HashSet<ModelId> characterCardIds = owner.Character.CardPool.AllCardIds.ToHashSet();
                candidates = candidates.Where(sample => characterCardIds.Contains(sample.Card.Id));
            }
        }

        return candidates;
    }

    public static IReadOnlyList<CardModel> GetChallengeCandidates(Player owner)
    {
        EnsureTrained();
        return FilterCandidates(owner)
            .Select(sample => sample.Card)
            .OrderBy(card => card.Id.Entry, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task YieldProcessFrame()
    {
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }

    /// <summary>
    /// Builds the runtime fallback cache incrementally. Card portraits and Godot
    /// image APIs remain on the main thread, but yielding after each card keeps
    /// interactive UI (such as the tracing search field) responsive.
    /// </summary>
    public static Task EnsureTrainedAsync()
    {
        if (_samples != null)
        {
            return Task.CompletedTask;
        }

        return _asyncTrainingTask ??= EnsureTrainedAsyncCore();
    }

    private static async Task EnsureTrainedAsyncCore()
    {
        // Let the text-change handler return before any cache or model work.
        await YieldProcessFrame();
        if (_samples != null)
        {
            return;
        }

        Preload();
        List<CardModel> cards = GetEligibleCards();
        List<TrainingSample> samples = new(cards.Count);
        int fullyPretrained = 0;
        int dynamicHandcrafted = 0;
        int dynamicDino = 0;
        int skipped = 0;
        foreach (CardModel card in cards)
        {
            double[]? features = null;
            float[]? dinoFeatures = null;
            _pretrainedFeatures?.TryGetValue(card.Id.Entry, out features);
            _pretrainedDinoFeatures?.TryGetValue(card.Id.Entry, out dinoFeatures);
            bool hadHandcrafted = features != null;
            bool hadDino = dinoFeatures != null;
            if (features == null || dinoFeatures == null)
            {
                try
                {
                    Image? image = LoadCardPortraitImage(card);
                    if (image != null && !image.IsEmpty())
                    {
                        if (features == null)
                        {
                            features = ExtractFeatures(image, treatAsSketch: false);
                            dynamicHandcrafted++;
                        }
                        if (dinoFeatures == null && DinoArtEmbedder.TryExtract(image, out float[] extractedDinoFeatures))
                        {
                            dinoFeatures = extractedDinoFeatures;
                            dynamicDino++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Entry.Logger.Debug($"[DrawAndGuessMod] Could not dynamically extract classifier portrait {card.Id.Entry}: {ex.Message}");
                }
            }

            if (features == null)
            {
                skipped++;
            }
            else
            {
                if (hadHandcrafted && hadDino)
                {
                    fullyPretrained++;
                }
                samples.Add(new TrainingSample(card, features, dinoFeatures));
            }

            await YieldProcessFrame();
        }

        _samples = samples;
        Entry.Logger.Info($"[DrawAndGuessMod] Classifier ready with {_samples.Count} cards ({fullyPretrained} fully pre-trained, handcrafted dynamic={dynamicHandcrafted}, DINOv2 dynamic={dynamicDino}, skipped={skipped}).");
    }

    private static void EnsureTrained()
    {
        if (_samples != null)
        {
            return;
        }

        Preload();
        List<CardModel> cards = GetEligibleCards();

        _samples = new List<TrainingSample>(cards.Count);
        int fullyPretrained = 0;
        int skipped = 0;
        foreach (CardModel card in cards)
        {
            double[]? features = null;
            float[]? dinoFeatures = null;
            _pretrainedFeatures?.TryGetValue(card.Id.Entry, out features);
            _pretrainedDinoFeatures?.TryGetValue(card.Id.Entry, out dinoFeatures);
            if (features == null)
            {
                skipped++;
                continue;
            }
            if (dinoFeatures != null)
            {
                fullyPretrained++;
            }
            _samples.Add(new TrainingSample(card, features, dinoFeatures));
        }

        Entry.Logger.Info(
            $"[DrawAndGuessMod] Classifier ready with {_samples.Count} cached cards " +
            $"({fullyPretrained} with DINOv2, skipped={skipped}). " +
            "Run Card Recognition Cache to include newly installed mod cards.");
    }

    private static Dictionary<string, double[]> LoadPretrainedFeatures()
    {
        foreach (string path in CandidateModelPaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using FileStream stream = File.OpenRead(path);
                using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
                if (!reader.ReadBytes(4).SequenceEqual("DAGM"u8.ToArray()))
                {
                    throw new InvalidDataException("invalid magic");
                }

                int version = reader.ReadInt32();
                int featureCount = reader.ReadInt32();
                int sampleCount = reader.ReadInt32();
                if (version != ModelVersion || featureCount != FeatureCount || sampleCount < 0 || sampleCount > 10000)
                {
                    throw new InvalidDataException($"unsupported header version={version}, features={featureCount}, samples={sampleCount}");
                }

                Dictionary<string, double[]> result = new(sampleCount, StringComparer.Ordinal);
                for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                {
                    ushort idLength = reader.ReadUInt16();
                    string cardId = Encoding.UTF8.GetString(reader.ReadBytes(idLength));
                    double[] features = new double[featureCount];
                    for (int featureIndex = 0; featureIndex < featureCount; featureIndex++)
                    {
                        features[featureIndex] = reader.ReadSingle();
                    }
                    result[cardId] = features;
                }

                return result;
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn($"[DrawAndGuessMod] Failed to load pre-trained model '{path}': {ex.Message}");
            }
        }

        Entry.Logger.Warn(
            "[DrawAndGuessMod] Pre-trained card model was not found. " +
            "Run Card Recognition Cache before using card recognition.");
        return new Dictionary<string, double[]>(StringComparer.Ordinal);
    }

    private static Dictionary<string, float[]> LoadPretrainedDinoFeatures()
    {
        foreach (string path in CandidateDinoModelPaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using FileStream stream = File.OpenRead(path);
                using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
                if (!reader.ReadBytes(4).SequenceEqual("DAGD"u8.ToArray()))
                {
                    throw new InvalidDataException("invalid magic");
                }

                int version = reader.ReadInt32();
                int featureCount = reader.ReadInt32();
                int sampleCount = reader.ReadInt32();
                if (version != DinoModelVersion || featureCount != DinoArtEmbedder.EmbeddingSize || sampleCount < 0 || sampleCount > 10000)
                {
                    throw new InvalidDataException($"unsupported header version={version}, features={featureCount}, samples={sampleCount}");
                }

                Dictionary<string, float[]> result = new(sampleCount, StringComparer.Ordinal);
                for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                {
                    ushort idLength = reader.ReadUInt16();
                    string cardId = Encoding.UTF8.GetString(reader.ReadBytes(idLength));
                    float[] features = new float[featureCount];
                    for (int featureIndex = 0; featureIndex < featureCount; featureIndex++)
                    {
                        features[featureIndex] = reader.ReadSingle();
                    }
                    result[cardId] = features;
                }
                return result;
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn($"[DrawAndGuessMod] Failed to load DINOv2 features '{path}': {ex.Message}");
            }
        }

        Entry.Logger.Warn(
            "[DrawAndGuessMod] Pre-trained DINOv2 card features were not found. " +
            "Run Card Recognition Cache to build them.");
        return new Dictionary<string, float[]>(StringComparer.Ordinal);
    }

    private static IEnumerable<string> CandidateModelPaths()
    {
        yield return GetUserModelPath();
        string? assemblyDirectory = Path.GetDirectoryName(typeof(CardArtClassifier).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            yield return Path.Combine(assemblyDirectory, "Models", "card_features.bin");
        }
        yield return Path.Combine("mods", Entry.ModId, "Models", "card_features.bin");
    }

    private static IEnumerable<string> CandidateDinoModelPaths()
    {
        yield return GetUserDinoModelPath();
        string? assemblyDirectory = Path.GetDirectoryName(typeof(CardArtClassifier).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            yield return Path.Combine(assemblyDirectory, "Models", "card_dino_features.bin");
        }
        yield return Path.Combine("mods", Entry.ModId, "Models", "card_dino_features.bin");
    }

    private static List<CardModel> GetEligibleCards()
    {
        return ModelDb.AllCards
            .Concat(ModelDb.All.OfType<CardModel>())
            .Where(card =>
                card is not Blank &&
                !card.IsMock &&
                card.ShouldShowInCardLibrary &&
                card.Type != CardType.None)
            .GroupBy(card => card.Id)
            .Select(group => group.First())
            .OrderBy(card => card.Id.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    private static string GetUserModelPath()
    {
        return ProjectSettings.GlobalizePath($"user://mods/{Entry.ModId}/card_features.local.v2.bin");
    }

    private static string GetUserDinoModelPath()
    {
        return ProjectSettings.GlobalizePath($"user://mods/{Entry.ModId}/card_dino_features.local.v2.bin");
    }

    private static void SavePretrainedFeatures(string path, IReadOnlyDictionary<string, double[]> featuresByCardId)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = path + ".tmp";
        using (FileStream stream = File.Create(temporaryPath))
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: false))
        {
            writer.Write("DAGM"u8.ToArray());
            writer.Write(ModelVersion);
            writer.Write(FeatureCount);
            writer.Write(featuresByCardId.Count);
            foreach ((string cardId, double[] features) in featuresByCardId.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                byte[] encodedId = Encoding.UTF8.GetBytes(cardId);
                if (encodedId.Length > ushort.MaxValue || features.Length != FeatureCount)
                {
                    throw new InvalidDataException($"Cannot serialize card feature record {cardId}.");
                }

                writer.Write((ushort)encodedId.Length);
                writer.Write(encodedId);
                foreach (double feature in features)
                {
                    writer.Write((float)feature);
                }
            }
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void SavePretrainedDinoFeatures(string path, IReadOnlyDictionary<string, float[]> featuresByCardId)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = path + ".tmp";
        using (FileStream stream = File.Create(temporaryPath))
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: false))
        {
            writer.Write("DAGD"u8.ToArray());
            writer.Write(DinoModelVersion);
            writer.Write(DinoArtEmbedder.EmbeddingSize);
            writer.Write(featuresByCardId.Count);
            foreach ((string cardId, float[] features) in featuresByCardId.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                byte[] encodedId = Encoding.UTF8.GetBytes(cardId);
                if (encodedId.Length > ushort.MaxValue || features.Length != DinoArtEmbedder.EmbeddingSize)
                {
                    throw new InvalidDataException($"Cannot serialize DINOv2 feature record {cardId}.");
                }

                writer.Write((ushort)encodedId.Length);
                writer.Write(encodedId);
                foreach (float feature in features)
                {
                    writer.Write(feature);
                }
            }
        }
        File.Move(temporaryPath, path, overwrite: true);
    }

    /// <summary>
    /// Loads a card's original portrait as a readable RGBA image, bypassing the
    /// patched <see cref="CardModel.Portrait"/> getter so the result is always
    /// the game's art (used by the tracing reference panel and feature caches).
    /// </summary>
    internal static Image? LoadCardPortraitImage(CardModel card)
    {
        try
        {
            Texture2D? texture = ResourceLoader.Load<Texture2D>(
                card.PortraitPath,
                null,
                ResourceLoader.CacheMode.Reuse);
            Image? image = texture == null ? null : GetReadableImage(texture);
            if (image != null)
            {
                return image;
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Debug($"[DrawAndGuessMod] Failed to load portrait resource for {card.Id.Entry}: {ex.Message}");
        }

        return null;
    }

    private static Image? GetReadableImage(Texture2D texture)
    {
        if (texture is AtlasTexture atlasTexture && atlasTexture.Atlas != null)
        {
            Image atlasImage = atlasTexture.Atlas.GetImage();
            if (!MakeReadable(atlasImage))
            {
                return null;
            }

            Rect2 region = atlasTexture.Region;
            int width = Math.Max(1, Mathf.RoundToInt(region.Size.X));
            int height = Math.Max(1, Mathf.RoundToInt(region.Size.Y));
            Rect2I sourceRect = new(
                Mathf.RoundToInt(region.Position.X),
                Mathf.RoundToInt(region.Position.Y),
                width,
                height);
            Image cropped = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
            cropped.BlitRect(atlasImage, sourceRect, Vector2I.Zero);
            return cropped;
        }

        Image image = texture.GetImage();
        return MakeReadable(image) ? image : null;
    }

    private static bool MakeReadable(Image image)
    {
        if (image.IsEmpty())
        {
            return false;
        }

        if (image.IsCompressed() && image.Decompress() != Error.Ok)
        {
            return false;
        }

        image.Convert(Image.Format.Rgba8);
        return true;
    }

    internal static Image CopyBaseLayer(Image source)
    {
        if (source.HasMipmaps())
        {
            throw new InvalidOperationException(
                "Mipmapped GPU textures must be read through PortraitGpuImageLoader.");
        }

        Image copy = Image.CreateFromData(
            source.GetWidth(),
            source.GetHeight(),
            false,
            source.GetFormat(),
            source.GetData());
        if (copy.IsCompressed() && copy.Decompress() != Error.Ok)
        {
            copy.Dispose();
            throw new InvalidDataException("Could not decompress copied image.");
        }
        return copy;
    }

    private static Image CreatePretrainingThumbnail(Image source)
    {
        using Image scaled = CopyBaseLayer(source);
        if (scaled.IsCompressed())
        {
            scaled.Decompress();
        }
        scaled.Convert(Image.Format.Rgba8);

        float scale = Math.Min(
            PretrainingThumbnailWidth / (float)Math.Max(1, scaled.GetWidth()),
            PretrainingThumbnailHeight / (float)Math.Max(1, scaled.GetHeight()));
        int width = Math.Max(1, Mathf.RoundToInt(scaled.GetWidth() * scale));
        int height = Math.Max(1, Mathf.RoundToInt(scaled.GetHeight() * scale));
        scaled.Resize(width, height, Image.Interpolation.Bilinear);

        Image thumbnail = Image.CreateEmpty(
            PretrainingThumbnailWidth,
            PretrainingThumbnailHeight,
            false,
            Image.Format.Rgba8);
        thumbnail.Fill(Colors.Transparent);
        thumbnail.BlitRect(
            scaled,
            new Rect2I(Vector2I.Zero, new Vector2I(width, height)),
            new Vector2I(
                (PretrainingThumbnailWidth - width) / 2,
                (PretrainingThumbnailHeight - height) / 2));
        return thumbnail;
    }

    internal static double[] ExtractFeatures(Image source, bool treatAsSketch)
    {
        using Image image = CopyBaseLayer(source);
        if (image.IsCompressed())
        {
            image.Decompress();
        }
        image.Convert(Image.Format.Rgba8);
        image.Resize(SampleSize, SampleSize, Image.Interpolation.Lanczos);

        double[,] luminance = new double[SampleSize, SampleSize];
        Color[,] pixels = new Color[SampleSize, SampleSize];
        for (int y = 0; y < SampleSize; y++)
        {
            for (int x = 0; x < SampleSize; x++)
            {
                Color pixel = image.GetPixel(x, y);
                pixels[x, y] = pixel;
                luminance[x, y] = pixel.R * 0.299 + pixel.G * 0.587 + pixel.B * 0.114;
            }
        }

        double[] edgeFeatures = new double[EdgeFeatureCount];
        double[,] edgeStrength = new double[SampleSize, SampleSize];
        double total = 0d;
        double weightedX = 0d;
        double weightedY = 0d;
        double left = 0d;
        double right = 0d;
        double top = 0d;
        double bottom = 0d;

        for (int y = 0; y < SampleSize; y++)
        {
            for (int x = 0; x < SampleSize; x++)
            {
                int x0 = Math.Max(0, x - 1);
                int x1 = Math.Min(SampleSize - 1, x + 1);
                int y0 = Math.Max(0, y - 1);
                int y1 = Math.Min(SampleSize - 1, y + 1);
                double gx = luminance[x1, y] - luminance[x0, y];
                double gy = luminance[x, y1] - luminance[x, y0];
                double strength = Math.Clamp(Math.Sqrt(gx * gx + gy * gy) * 2.4, 0d, 1d);
                if (treatAsSketch)
                {
                    double ink = Math.Clamp((0.94d - luminance[x, y]) / 0.94d, 0d, 1d);
                    strength = Math.Clamp(strength + ink * 0.18d, 0d, 1d);
                }
                edgeStrength[x, y] = strength;

                int cellX = x * GridSize / SampleSize;
                int cellY = y * GridSize / SampleSize;
                edgeFeatures[cellY * GridSize + cellX] += strength;
                total += strength;
                weightedX += strength * x;
                weightedY += strength * y;
                if (x < SampleSize / 2) left += strength; else right += strength;
                if (y < SampleSize / 2) top += strength; else bottom += strength;
            }
        }

        double cellArea = SampleSize * SampleSize / (double)(GridSize * GridSize);
        for (int i = 0; i < GridSize * GridSize; i++)
        {
            edgeFeatures[i] /= cellArea;
        }

        double safeTotal = Math.Max(total, 0.0001d);
        int extra = GridSize * GridSize;
        edgeFeatures[extra] = total / (SampleSize * SampleSize);
        edgeFeatures[extra + 1] = weightedX / safeTotal / (SampleSize - 1);
        edgeFeatures[extra + 2] = weightedY / safeTotal / (SampleSize - 1);
        edgeFeatures[extra + 3] = (left - right) / safeTotal;
        edgeFeatures[extra + 4] = (top - bottom) / safeTotal;
        edgeFeatures[extra + 5] = CountOccupiedCells(edgeFeatures);
        Normalize(edgeFeatures);

        double[] fineEdgeFeatures = new double[FineEdgeFeatureCount];
        double fineCellArea = SampleSize * SampleSize / (double)(FineGridSize * FineGridSize);
        for (int y = 0; y < SampleSize; y++)
        {
            for (int x = 0; x < SampleSize; x++)
            {
                int cellX = x * FineGridSize / SampleSize;
                int cellY = y * FineGridSize / SampleSize;
                fineEdgeFeatures[cellY * FineGridSize + cellX] += edgeStrength[x, y] / fineCellArea;
            }
        }
        Normalize(fineEdgeFeatures);

        double[] colorFeatures = ExtractColorFeatures(pixels);
        double[] features = new double[FeatureCount];
        for (int i = 0; i < edgeFeatures.Length; i++)
        {
            features[i] = edgeFeatures[i] * 0.55d;
        }
        for (int i = 0; i < fineEdgeFeatures.Length; i++)
        {
            features[EdgeFeatureCount + i] = fineEdgeFeatures[i] * 0.55d;
        }
        for (int i = 0; i < colorFeatures.Length; i++)
        {
            features[EdgeFeatureCount + FineEdgeFeatureCount + i] = colorFeatures[i] * 0.6284902545d;
        }
        return features;
    }

    private static double[] ExtractColorFeatures(Color[,] pixels)
    {
        double[] features = new double[ColorFeatureCount];
        double pixelCount = SampleSize * SampleSize;
        double colorCellArea = pixelCount / (ColorGridSize * ColorGridSize);
        int hueHistogramOffset = ColorGridSize * ColorGridSize * 3;
        double coverage = 0d;
        double weightedValue = 0d;

        for (int y = 0; y < SampleSize; y++)
        {
            for (int x = 0; x < SampleSize; x++)
            {
                Color pixel = pixels[x, y];
                double red = pixel.R;
                double green = pixel.G;
                double blue = pixel.B;
                double value = Math.Max(red, Math.Max(green, blue));
                double minimum = Math.Min(red, Math.Min(green, blue));
                double delta = value - minimum;
                double saturation = value > 0.000001d ? delta / value : 0d;
                double colorWeight = Math.Clamp((saturation - 0.15d) / 0.85d, 0d, 1d);

                int cellX = x * ColorGridSize / SampleSize;
                int cellY = y * ColorGridSize / SampleSize;
                int cellOffset = (cellY * ColorGridSize + cellX) * 3;
                features[cellOffset] += colorWeight * red / colorCellArea;
                features[cellOffset + 1] += colorWeight * green / colorCellArea;
                features[cellOffset + 2] += colorWeight * blue / colorCellArea;

                double hue = CalculateHue(red, green, blue, value, delta);
                int hueBin = Math.Min((int)(hue * 8d), 7);
                features[hueHistogramOffset + hueBin] += colorWeight * value / pixelCount;
                coverage += colorWeight / pixelCount;
                weightedValue += colorWeight * value / pixelCount;
            }
        }

        features[^2] = coverage;
        features[^1] = weightedValue;
        Normalize(features);
        return features;
    }

    private static double CalculateHue(double red, double green, double blue, double value, double delta)
    {
        if (delta <= 0.000001d)
        {
            return 0d;
        }

        double hue;
        if (red >= green && red >= blue)
        {
            hue = ((green - blue) / delta) % 6d;
        }
        else if (green >= blue)
        {
            hue = (blue - red) / delta + 2d;
        }
        else
        {
            hue = (red - green) / delta + 4d;
        }

        hue /= 6d;
        return hue < 0d ? hue + 1d : hue;
    }

    private static double CountOccupiedCells(double[] features)
    {
        int occupied = 0;
        for (int i = 0; i < GridSize * GridSize; i++)
        {
            if (features[i] > 0.08d)
            {
                occupied++;
            }
        }
        return occupied / (double)(GridSize * GridSize);
    }

    private static void Normalize(double[] values)
    {
        double length = Math.Sqrt(values.Sum(value => value * value));
        if (length <= 0.000001d)
        {
            return;
        }

        for (int i = 0; i < values.Length; i++)
        {
            values[i] /= length;
        }
    }

    internal static double Distance(double[] left, double[] right)
    {
        double sum = 0d;
        for (int i = 0; i < left.Length; i++)
        {
            double delta = left[i] - right[i];
            sum += delta * delta;
        }
        return Math.Sqrt(sum);
    }

    private static double CosineSimilarity(float[] left, float[] right)
    {
        double dot = 0d;
        int count = Math.Min(left.Length, right.Length);
        for (int index = 0; index < count; index++)
        {
            dot += left[index] * right[index];
        }
        return dot;
    }

    private static double[] Standardize(double[] values)
    {
        if (values.Length == 0)
        {
            return [];
        }
        double mean = values.Average();
        double variance = values.Sum(value => (value - mean) * (value - mean)) / values.Length;
        double standardDeviation = Math.Sqrt(variance);
        if (standardDeviation <= 0.0000001d)
        {
            return new double[values.Length];
        }
        return values.Select(value => (value - mean) / standardDeviation).ToArray();
    }

    private static double[] StandardizeAvailable(double[] values, bool[] available)
    {
        double[] present = values.Where((_, index) => available[index]).ToArray();
        double[] standardizedPresent = Standardize(present);
        double[] result = new double[values.Length];
        int presentIndex = 0;
        for (int index = 0; index < values.Length; index++)
        {
            if (available[index])
            {
                result[index] = standardizedPresent[presentIndex++];
            }
        }
        return result;
    }

    private sealed record TrainingSample(CardModel Card, double[] Features, float[]? DinoFeatures);
    private sealed class PortraitReadbackUnavailableException : InvalidOperationException
    {
        public PortraitReadbackUnavailableException(string message) : base(message)
        {
        }
    }

    private sealed class PortraitGpuImageLoader : IDisposable
    {
        private const int MaximumReadbackDimension = 1024;
        private readonly SceneTree _tree;
        private readonly SubViewport _viewport;
        private readonly TextureRect _textureRect;
        private Texture2D? _loadedTexture;

        public PortraitGpuImageLoader()
        {
            _tree = Engine.GetMainLoop() as SceneTree
                ?? throw new InvalidOperationException("Card-art GPU readback requires an active SceneTree.");
            _viewport = new SubViewport
            {
                Name = "DrawAndGuessMod_CardArtReadback",
                TransparentBg = true,
                Disable3D = true,
                HandleInputLocally = false,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled
            };
            _textureRect = new TextureRect
            {
                Position = Vector2.Zero,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                TextureFilter = CanvasItem.TextureFilterEnum.Linear
            };
            _viewport.AddChild(_textureRect);
            _tree.Root.AddChild(_viewport);
        }

        public async Task<Image?> LoadAsync(string path)
        {
            EnsureReadbackAvailable();
            ReleaseLoadedTexture();
            Texture2D? texture = ResourceLoader.Load<Texture2D>(
                path,
                null,
                ResourceLoader.CacheMode.Ignore);
            if (texture == null)
            {
                return null;
            }
            _loadedTexture = texture;
            if (texture.GetWidth() <= 0 || texture.GetHeight() <= 0)
            {
                ReleaseLoadedTexture();
                return null;
            }

            Vector2I readbackSize = CalculateReadbackSize(texture.GetWidth(), texture.GetHeight());
            Image? image = null;
            try
            {
                _viewport.Size = readbackSize;
                _textureRect.Size = readbackSize;
                _textureRect.TextureFilter =
                    readbackSize.X == texture.GetWidth() && readbackSize.Y == texture.GetHeight()
                        ? CanvasItem.TextureFilterEnum.Nearest
                        : CanvasItem.TextureFilterEnum.Linear;
                _textureRect.Texture = texture;
                _viewport.RenderTargetClearMode = SubViewport.ClearMode.Always;
                _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;

                await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
                EnsureReadbackAvailable();
                await _viewport.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                EnsureReadbackAvailable();

                image = _viewport.GetTexture().GetImage();
                if (image == null || image.IsEmpty())
                {
                    image?.Dispose();
                    return null;
                }
                if (image.IsCompressed() && image.Decompress() != Error.Ok)
                {
                    image.Dispose();
                    image = null;
                    return null;
                }
                image.Convert(Image.Format.Rgba8);
                Image result = image;
                image = null;
                return result;
            }
            finally
            {
                image?.Dispose();
                if (GodotObject.IsInstanceValid(_textureRect))
                {
                    _textureRect.Texture = null;
                }
                if (GodotObject.IsInstanceValid(_viewport))
                {
                    _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                }
                ReleaseLoadedTexture();
            }
        }

        public void Dispose()
        {
            if (GodotObject.IsInstanceValid(_textureRect))
            {
                _textureRect.Texture = null;
            }
            ReleaseLoadedTexture();
            if (GodotObject.IsInstanceValid(_viewport))
            {
                _viewport.QueueFree();
            }
        }

        private static Vector2I CalculateReadbackSize(int sourceWidth, int sourceHeight)
        {
            sourceWidth = Math.Max(1, sourceWidth);
            sourceHeight = Math.Max(1, sourceHeight);
            int largestDimension = Math.Max(sourceWidth, sourceHeight);
            if (largestDimension <= MaximumReadbackDimension)
            {
                return new Vector2I(sourceWidth, sourceHeight);
            }

            float scale = MaximumReadbackDimension / (float)largestDimension;
            return new Vector2I(
                Math.Max(1, Mathf.RoundToInt(sourceWidth * scale)),
                Math.Max(1, Mathf.RoundToInt(sourceHeight * scale)));
        }

        private void ReleaseLoadedTexture()
        {
            _loadedTexture?.Dispose();
            _loadedTexture = null;
        }

        private void EnsureReadbackAvailable()
        {
            if (!GodotObject.IsInstanceValid(_tree) ||
                !GodotObject.IsInstanceValid(_tree.Root) ||
                !GodotObject.IsInstanceValid(_viewport) ||
                !_viewport.IsInsideTree() ||
                !GodotObject.IsInstanceValid(_textureRect))
            {
                throw new PortraitReadbackUnavailableException(
                    "Card-art GPU readback stopped because the game scene tree is no longer available.");
            }
        }
    }

    private sealed record RankedCandidate(
        TrainingSample Sample,
        double CurrentDistance,
        double CurrentScore,
        double DinoSimilarity,
        double DinoScore,
        bool HasDino,
        double FusedScore);
}
