using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Ai;

internal sealed record RelicArtAssessment(
    double TargetDistance,
    double SimilarityPercent,
    bool IsAccepted);

internal static class RelicArtClassifier
{
    public const double RequiredSimilarityPercent = 85d;
    private const int RecognitionSize = 224;
    private const int ArtworkSize = 192;
    private const int ModelVersion = 2;
    private const int BaseFeatureCount = 384;
    private const int FeatureCount = BaseFeatureCount * 3;
    private const double WhiteBackgroundWeight = 0.45d;
    private const double DarkBackgroundWeight = 0.30d;
    private const double AlphaMaskWeight = 0.25d;
    private static readonly Color DarkRecognitionBackground =
        new(0.12f, 0.14f, 0.18f, 1f);
    private static readonly object SamplesLock = new();
    private static List<RelicSample>? _samples;
    private static Dictionary<string, double[]>? _pretrainedFeatures;

    public static void Preload()
    {
        _pretrainedFeatures ??= LoadPretrainedFeatures();
    }

    public static IReadOnlyList<RelicModel> GetEligibleRelics()
    {
        EnsureSamples();
        return _samples?.Select(sample => sample.Relic).ToList() ?? [];
    }

    private static IReadOnlyList<RelicModel> GetRawEligibleRelics()
    {
        return ModelDb.AllRelics
            .Where(relic => !IsMockModel(relic))
            .OrderBy(relic => relic.Id.Entry, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsMockModel(RelicModel relic)
    {
        Type type = relic.GetType();
        string fullName = type.FullName ?? string.Empty;
        return relic.Id.ToString().Contains("mock", StringComparison.OrdinalIgnoreCase)
               || type.Name.Contains("mock", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains(".Mocks.", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains(".Mock.", StringComparison.OrdinalIgnoreCase);
    }

    public static RelicArtAssessment? AssessTarget(Image drawing, RelicModel target)
    {
        EnsureSamples();
        if (_samples == null || _samples.Count == 0)
        {
            return null;
        }

        RelicSample? targetSample = _samples.FirstOrDefault(
            sample => sample.Relic.Id == target.Id);
        if (targetSample == null)
        {
            return null;
        }

        double[] drawingFeatures =
            ExtractRelicFeatures(drawing, treatAsSketch: true);
        double targetDistance = CardArtClassifier.Distance(
            drawingFeatures,
            targetSample.Features);
        double similarityPercent = Math.Clamp(
            (1d - targetDistance * targetDistance * 0.5d) * 100d,
            0d,
            100d);
        return new RelicArtAssessment(
            targetDistance,
            similarityPercent,
            similarityPercent >= RequiredSimilarityPercent);
    }

    private static void EnsureSamples()
    {
        if (_samples != null)
        {
            return;
        }

        lock (SamplesLock)
        {
            if (_samples != null)
            {
                return;
            }

            Preload();
            List<RelicSample> samples = new();
            int pretrained = 0;
            int dynamic = 0;
            foreach (RelicModel relic in GetRawEligibleRelics())
            {
                double[]? features = null;
                if (_pretrainedFeatures != null &&
                    _pretrainedFeatures.TryGetValue(
                        relic.Id.Entry,
                        out double[]? cachedFeatures))
                {
                    features = cachedFeatures;
                    pretrained++;
                }

                try
                {
                    if (features == null)
                    {
                        Image source = relic.BigIcon.GetImage();
                        if (!source.IsEmpty())
                        {
                            features = ExtractRelicFeatures(
                                source,
                                treatAsSketch: false);
                            dynamic++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Entry.Logger.Debug(
                        $"[DrawAndGuessMod] Could not extract relic artwork {relic.Id.Entry}: {ex.Message}");
                }

                if (features != null)
                {
                    samples.Add(new RelicSample(relic, features));
                }
            }

            _samples = samples;
            Entry.Logger.Info(
                $"[DrawAndGuessMod] Relic artwork classifier ready with {samples.Count} relics " +
                $"(preprocessed={pretrained}, dynamic={dynamic}).");
        }
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
                using BinaryReader reader = new(
                    stream,
                    Encoding.UTF8,
                    leaveOpen: false);
                if (!reader.ReadBytes(4).SequenceEqual("DAGR"u8.ToArray()))
                {
                    throw new InvalidDataException("invalid magic");
                }

                int version = reader.ReadInt32();
                int featureCount = reader.ReadInt32();
                int sampleCount = reader.ReadInt32();
                if (version != ModelVersion ||
                    featureCount != FeatureCount ||
                    sampleCount < 0 ||
                    sampleCount > 10000)
                {
                    throw new InvalidDataException(
                        $"unsupported header version={version}, " +
                        $"features={featureCount}, samples={sampleCount}");
                }

                Dictionary<string, double[]> result =
                    new(sampleCount, StringComparer.Ordinal);
                for (int sampleIndex = 0;
                     sampleIndex < sampleCount;
                     sampleIndex++)
                {
                    ushort idLength = reader.ReadUInt16();
                    string relicId = Encoding.UTF8.GetString(
                        reader.ReadBytes(idLength));
                    double[] features = new double[featureCount];
                    for (int featureIndex = 0;
                         featureIndex < featureCount;
                         featureIndex++)
                    {
                        features[featureIndex] = reader.ReadSingle();
                    }
                    result[relicId] = features;
                }

                Entry.Logger.Info(
                    $"[DrawAndGuessMod] Preloaded {result.Count} relic-art feature records.");
                return result;
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn(
                    $"[DrawAndGuessMod] Failed to load preprocessed relic model " +
                    $"'{path}': {ex.Message}");
            }
        }

        Entry.Logger.Warn(
            "[DrawAndGuessMod] Preprocessed relic model was not found; " +
            "falling back to runtime feature extraction.");
        return new Dictionary<string, double[]>(StringComparer.Ordinal);
    }

    private static IEnumerable<string> CandidateModelPaths()
    {
        string? assemblyDirectory = Path.GetDirectoryName(
            typeof(RelicArtClassifier).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            yield return Path.Combine(
                assemblyDirectory,
                "Models",
                "relic_features.bin");
        }
        yield return Path.Combine(
            "mods",
            Entry.ModId,
            "Models",
            "relic_features.bin");
    }

    private static double[] ExtractRelicFeatures(
        Image source,
        bool treatAsSketch)
    {
        NormalizedRelicArtwork normalized =
            NormalizeTransparentArtwork(source);
        double[] whiteFeatures = CardArtClassifier.ExtractFeatures(
            normalized.WhiteBackground,
            treatAsSketch);
        double[] darkFeatures = CardArtClassifier.ExtractFeatures(
            normalized.DarkBackground,
            treatAsSketch);
        double[] alphaFeatures = CardArtClassifier.ExtractFeatures(
            normalized.AlphaMask,
            treatAsSketch: false);

        double[] combined = new double[FeatureCount];
        CopyWeightedFeatures(
            whiteFeatures,
            combined,
            0,
            WhiteBackgroundWeight);
        CopyWeightedFeatures(
            darkFeatures,
            combined,
            BaseFeatureCount,
            DarkBackgroundWeight);
        CopyWeightedFeatures(
            alphaFeatures,
            combined,
            BaseFeatureCount * 2,
            AlphaMaskWeight);
        return combined;
    }

    private static void CopyWeightedFeatures(
        IReadOnlyList<double> source,
        double[] destination,
        int destinationOffset,
        double weight)
    {
        if (source.Count != BaseFeatureCount)
        {
            throw new InvalidDataException(
                $"Unexpected relic feature count {source.Count}; " +
                $"expected {BaseFeatureCount}.");
        }

        double scale = Math.Sqrt(weight);
        for (int index = 0; index < source.Count; index++)
        {
            destination[destinationOffset + index] =
                source[index] * scale;
        }
    }

    private static NormalizedRelicArtwork NormalizeTransparentArtwork(
        Image source)
    {
        Image image = Image.CreateFromData(
            source.GetWidth(),
            source.GetHeight(),
            source.HasMipmaps(),
            source.GetFormat(),
            source.GetData());
        if (image.IsCompressed() && image.Decompress() != Error.Ok)
        {
            return CreateBlankNormalizedArtwork();
        }

        image.Convert(Image.Format.Rgba8);
        Rect2I contentBounds = FindContentBounds(image);
        if (contentBounds.Size.X <= 0 || contentBounds.Size.Y <= 0)
        {
            return CreateBlankNormalizedArtwork();
        }

        Image artwork = image.GetRegion(contentBounds);
        float scale = ArtworkSize / (float)Math.Max(artwork.GetWidth(), artwork.GetHeight());
        int width = Math.Max(1, Mathf.RoundToInt(artwork.GetWidth() * scale));
        int height = Math.Max(1, Mathf.RoundToInt(artwork.GetHeight() * scale));
        artwork.Resize(width, height, Image.Interpolation.Lanczos);

        Vector2I destination =
            new(
                (RecognitionSize - width) / 2,
                (RecognitionSize - height) / 2);
        Rect2I sourceRect = new(0, 0, width, height);

        Image whiteBackground =
            CreateRecognitionImage(Colors.White);
        whiteBackground.BlendRect(
            artwork,
            sourceRect,
            destination);

        Image darkBackground =
            CreateRecognitionImage(DarkRecognitionBackground);
        darkBackground.BlendRect(
            artwork,
            sourceRect,
            destination);

        Image alphaMask = CreateRecognitionImage(Colors.White);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float alpha = artwork.GetPixel(x, y).A;
                float value = 1f - alpha;
                alphaMask.SetPixel(
                    destination.X + x,
                    destination.Y + y,
                    new Color(value, value, value, 1f));
            }
        }

        return new NormalizedRelicArtwork(
            whiteBackground,
            darkBackground,
            alphaMask);
    }

    private static Rect2I FindContentBounds(Image image)
    {
        int minX = image.GetWidth();
        int minY = image.GetHeight();
        int maxX = -1;
        int maxY = -1;
        byte[] pixels = image.GetData();
        const byte alphaThreshold = 5;
        for (int y = 0; y < image.GetHeight(); y++)
        {
            for (int x = 0; x < image.GetWidth(); x++)
            {
                if (pixels[(y * image.GetWidth() + x) * 4 + 3] <= alphaThreshold)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxX < minX || maxY < minY
            ? new Rect2I()
            : new Rect2I(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static NormalizedRelicArtwork CreateBlankNormalizedArtwork()
    {
        return new NormalizedRelicArtwork(
            CreateRecognitionImage(Colors.White),
            CreateRecognitionImage(DarkRecognitionBackground),
            CreateRecognitionImage(Colors.White));
    }

    private static Image CreateRecognitionImage(Color background)
    {
        Image image = Image.CreateEmpty(RecognitionSize, RecognitionSize, false, Image.Format.Rgba8);
        image.Fill(background);
        return image;
    }

    private sealed record NormalizedRelicArtwork(
        Image WhiteBackground,
        Image DarkBackground,
        Image AlphaMask);

    private sealed record RelicSample(RelicModel Relic, double[] Features);
}
