using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Ai;

internal sealed record RelicArtGuess(RelicModel Relic, double Distance);

internal static class RelicArtClassifier
{
    private const int RecognitionSize = 224;
    private const int ArtworkSize = 192;
    private static readonly object SamplesLock = new();
    private static List<RelicSample>? _samples;

    public static IReadOnlyList<RelicModel> GetEligibleRelics()
    {
        EnsureSamples();
        return _samples?.Select(sample => sample.Relic).ToList() ?? [];
    }

    private static IReadOnlyList<RelicModel> GetRawEligibleRelics()
    {
        return ModelDb.AllRelics
            .Where(relic => relic.IsTradable)
            .Where(relic => relic.Rarity is
                RelicRarity.Common or
                RelicRarity.Uncommon or
                RelicRarity.Rare or
                RelicRarity.Shop)
            .OrderBy(relic => relic.Id.Entry, StringComparer.Ordinal)
            .ToList();
    }

    public static RelicArtGuess? GuessTopOne(Image drawing, IReadOnlySet<ModelId>? allowedIds = null)
    {
        EnsureSamples();
        if (_samples == null || _samples.Count == 0)
        {
            return null;
        }

        Image normalized = NormalizeTransparentArtwork(drawing);
        double[] drawingFeatures = CardArtClassifier.ExtractFeatures(normalized, treatAsSketch: true);
        RelicSample? nearest = null;
        double nearestDistance = double.MaxValue;
        foreach (RelicSample sample in _samples)
        {
            if (allowedIds != null && !allowedIds.Contains(sample.Relic.Id))
            {
                continue;
            }

            double distance = CardArtClassifier.Distance(drawingFeatures, sample.Features);
            if (distance < nearestDistance)
            {
                nearest = sample;
                nearestDistance = distance;
            }
        }

        return nearest == null ? null : new RelicArtGuess(nearest.Relic, nearestDistance);
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

            List<RelicSample> samples = new();
            foreach (RelicModel relic in GetRawEligibleRelics())
            {
                try
                {
                    Image source = relic.BigIcon.GetImage();
                    if (source.IsEmpty())
                    {
                        continue;
                    }

                    Image normalized = NormalizeTransparentArtwork(source);
                    samples.Add(new RelicSample(
                        relic,
                        CardArtClassifier.ExtractFeatures(normalized, treatAsSketch: false)));
                }
                catch (Exception ex)
                {
                    Entry.Logger.Debug(
                        $"[DrawAndGuessMod] Could not extract relic artwork {relic.Id.Entry}: {ex.Message}");
                }
            }

            _samples = samples;
            Entry.Logger.Info($"[DrawAndGuessMod] Relic artwork classifier ready with {samples.Count} relics.");
        }
    }

    private static Image NormalizeTransparentArtwork(Image source)
    {
        Image image = Image.CreateFromData(
            source.GetWidth(),
            source.GetHeight(),
            source.HasMipmaps(),
            source.GetFormat(),
            source.GetData());
        if (image.IsCompressed() && image.Decompress() != Error.Ok)
        {
            return CreateWhiteRecognitionImage();
        }

        image.Convert(Image.Format.Rgba8);
        Rect2I contentBounds = FindContentBounds(image);
        if (contentBounds.Size.X <= 0 || contentBounds.Size.Y <= 0)
        {
            return CreateWhiteRecognitionImage();
        }

        Image artwork = image.GetRegion(contentBounds);
        float scale = ArtworkSize / (float)Math.Max(artwork.GetWidth(), artwork.GetHeight());
        int width = Math.Max(1, Mathf.RoundToInt(artwork.GetWidth() * scale));
        int height = Math.Max(1, Mathf.RoundToInt(artwork.GetHeight() * scale));
        artwork.Resize(width, height, Image.Interpolation.Lanczos);

        Image normalized = CreateWhiteRecognitionImage();
        normalized.BlendRect(
            artwork,
            new Rect2I(0, 0, width, height),
            new Vector2I((RecognitionSize - width) / 2, (RecognitionSize - height) / 2));
        return normalized;
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

    private static Image CreateWhiteRecognitionImage()
    {
        Image image = Image.CreateEmpty(RecognitionSize, RecognitionSize, false, Image.Format.Rgba8);
        image.Fill(Colors.White);
        return image;
    }

    private sealed record RelicSample(RelicModel Relic, double[] Features);
}
