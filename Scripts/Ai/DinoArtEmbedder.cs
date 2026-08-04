using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Microsoft.ML.OnnxRuntime;

namespace DrawAndGuessMod.Scripts.Ai;

internal static class DinoArtEmbedder
{
    public const int EmbeddingSize = 384;
    private const int InputSize = 224;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] StandardDeviation = [0.229f, 0.224f, 0.225f];
    private static readonly object SessionLock = new();
    private static InferenceSession? _session;
    private static bool _disabled;

    public static void Preload()
    {
        if (_disabled)
        {
            return;
        }

        try
        {
            _ = GetOrCreateSession();
            using Image warmupImage = Image.CreateEmpty(InputSize, InputSize, false, Image.Format.Rgb8);
            warmupImage.Fill(Colors.White);
            if (TryExtract(warmupImage, out _))
            {
                Entry.Logger.Info("[DrawAndGuessMod] DINOv2 ONNX encoder warm-up completed.");
            }
        }
        catch (Exception ex)
        {
            Disable(ex);
        }
    }

    public static bool TryExtract(Image source, out float[] embedding)
    {
        embedding = [];
        if (_disabled || source.IsEmpty())
        {
            return false;
        }

        try
        {
            InferenceSession session = GetOrCreateSession();
            float[] inputBuffer = BuildInput(source);
            long[] inputShape = [1, 3, InputSize, InputSize];
            using OrtValue input = OrtValue.CreateTensorValueFromMemory(inputBuffer, inputShape);
            using RunOptions runOptions = new();
            using IDisposableReadOnlyCollection<OrtValue> results = session.Run(
                runOptions,
                ["image"],
                [input],
                ["embedding"]);
            ReadOnlySpan<float> output = results.First().GetTensorDataAsSpan<float>();
            if (output.Length != EmbeddingSize)
            {
                throw new InvalidDataException($"DINOv2 returned {output.Length} values instead of {EmbeddingSize}.");
            }

            embedding = output.ToArray();
            Normalize(embedding);
            return true;
        }
        catch (Exception ex)
        {
            Disable(ex);
            return false;
        }
    }

    private static void Disable(Exception ex)
    {
        _disabled = true;
        Entry.Logger.Warn($"[DrawAndGuessMod] DINOv2 unavailable; using handcrafted features only: {ex}");
    }

    private static InferenceSession GetOrCreateSession()
    {
        lock (SessionLock)
        {
            if (_session != null)
            {
                return _session;
            }

            string? modelPath = CandidateModelPaths().FirstOrDefault(File.Exists);
            if (modelPath == null)
            {
                throw new FileNotFoundException("Models/dinov2_vits14.onnx was not found.");
            }

            SessionOptions options = new()
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Clamp(System.Environment.ProcessorCount / 2, 1, 4),
                InterOpNumThreads = 1
            };
            _session = new InferenceSession(modelPath, options);
            Entry.Logger.Info($"[DrawAndGuessMod] Loaded DINOv2 ONNX encoder from {modelPath}.");
            return _session;
        }
    }

    private static IEnumerable<string> CandidateModelPaths()
    {
        string? assemblyDirectory = Path.GetDirectoryName(typeof(DinoArtEmbedder).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            yield return Path.Combine(assemblyDirectory, "Models", "dinov2_vits14.onnx");
        }
        yield return Path.Combine("mods", Entry.ModId, "Models", "dinov2_vits14.onnx");
    }

    private static float[] BuildInput(Image source)
    {
        using Image image = CardArtClassifier.CopyBaseLayer(source);
        if (image.IsCompressed() && image.Decompress() != Error.Ok)
        {
            throw new InvalidDataException("Could not decompress image for DINOv2.");
        }
        image.Convert(Image.Format.Rgb8);

        int width = image.GetWidth();
        int height = image.GetHeight();
        float scale = InputSize / (float)Math.Min(width, height);
        int resizedWidth = Math.Max(InputSize, Mathf.RoundToInt(width * scale));
        int resizedHeight = Math.Max(InputSize, Mathf.RoundToInt(height * scale));
        image.Resize(resizedWidth, resizedHeight, Image.Interpolation.Cubic);
        int cropX = Math.Max(0, (resizedWidth - InputSize) / 2);
        int cropY = Math.Max(0, (resizedHeight - InputSize) / 2);

        float[] input = new float[3 * InputSize * InputSize];
        int planeSize = InputSize * InputSize;
        for (int y = 0; y < InputSize; y++)
        {
            for (int x = 0; x < InputSize; x++)
            {
                Color pixel = image.GetPixel(cropX + x, cropY + y);
                int offset = y * InputSize + x;
                input[offset] = (pixel.R - Mean[0]) / StandardDeviation[0];
                input[planeSize + offset] = (pixel.G - Mean[1]) / StandardDeviation[1];
                input[planeSize * 2 + offset] = (pixel.B - Mean[2]) / StandardDeviation[2];
            }
        }
        return input;
    }

    private static void Normalize(float[] values)
    {
        double lengthSquared = 0d;
        foreach (float value in values)
        {
            lengthSquared += value * value;
        }
        double length = Math.Sqrt(lengthSquared);
        if (length <= 0.000001d)
        {
            return;
        }
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = (float)(values[index] / length);
        }
    }
}
