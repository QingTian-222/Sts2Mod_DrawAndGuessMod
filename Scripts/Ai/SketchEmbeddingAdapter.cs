using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;

namespace DrawAndGuessMod.Scripts.Ai;

internal static class SketchEmbeddingAdapter
{
    private const string ModelFileName = "sketch_adapter.onnx";
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
            float[] warmup = new float[DinoArtEmbedder.EmbeddingSize];
            warmup[0] = 1f;
            if (TryAdapt(warmup, out _))
            {
                Entry.Logger.Info("[DrawAndGuessMod] Sketch adapter ONNX warm-up completed.");
            }
        }
        catch (Exception ex)
        {
            Disable(ex);
        }
    }

    public static bool TryAdapt(float[] embedding, out float[] adaptedEmbedding)
    {
        adaptedEmbedding = [];
        if (_disabled || embedding.Length != DinoArtEmbedder.EmbeddingSize)
        {
            return false;
        }

        try
        {
            InferenceSession session = GetOrCreateSession();
            long[] inputShape = [1, DinoArtEmbedder.EmbeddingSize];
            using OrtValue input = OrtValue.CreateTensorValueFromMemory(embedding, inputShape);
            using RunOptions runOptions = new();
            using IDisposableReadOnlyCollection<OrtValue> results = session.Run(
                runOptions,
                ["embedding"],
                [input],
                ["adapted_embedding"]);
            ReadOnlySpan<float> output = results.First().GetTensorDataAsSpan<float>();
            if (output.Length != DinoArtEmbedder.EmbeddingSize)
            {
                throw new InvalidDataException(
                    $"Sketch adapter returned {output.Length} values instead of {DinoArtEmbedder.EmbeddingSize}.");
            }

            adaptedEmbedding = output.ToArray();
            return adaptedEmbedding.All(float.IsFinite);
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
        Entry.Logger.Warn(
            $"[DrawAndGuessMod] Sketch adapter unavailable; using the standard DINOv2 fusion: {ex}");
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
                throw new FileNotFoundException($"Models/{ModelFileName} was not found.");
            }

            SessionOptions options = new()
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Clamp(System.Environment.ProcessorCount / 2, 1, 4),
                InterOpNumThreads = 1
            };
            _session = new InferenceSession(modelPath, options);
            Entry.Logger.Info($"[DrawAndGuessMod] Loaded sketch adapter ONNX model from {modelPath}.");
            return _session;
        }
    }

    private static IEnumerable<string> CandidateModelPaths()
    {
        string? assemblyDirectory = Path.GetDirectoryName(typeof(SketchEmbeddingAdapter).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            yield return Path.Combine(assemblyDirectory, "Models", ModelFileName);
        }
        yield return Path.Combine("mods", Entry.ModId, "Models", ModelFileName);
    }
}
