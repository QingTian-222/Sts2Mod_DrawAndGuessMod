using System;
using System.Collections.Generic;
using System.Linq;
using DrawAndGuessMod.Scripts.Ui;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.RunData;

namespace DrawAndGuessMod.Scripts.State;

internal static class ArtworkStore
{
    private static RunSavedData<ArtworkRunState>? _savedData;
    private static RunState? _activeRun;
    private static readonly Dictionary<string, Texture2D> TextureCache = new(StringComparer.Ordinal);

    public static void Register()
    {
        _savedData = RitsuLibFramework.GetRunSavedDataStore(Entry.ModId).Register(
            "card_artworks",
            () => new ArtworkRunState(),
            new RunSavedDataOptions
            {
                SchemaVersion = 1,
                WritePolicy = RunSavedDataWritePolicy.WhenNonDefault
            });
    }

    public static void ActivateRun(RunState runState)
    {
        if (ReferenceEquals(_activeRun, runState))
        {
            return;
        }

        _activeRun = runState;
        TextureCache.Clear();
    }

    public static void Set(IRunState runState, CardModel card, byte[] pngBytes)
    {
        Set(runState, card.Id.Entry, AdaptToCardPortrait(card, pngBytes));
    }

    public static void Set(IRunState runState, string cardId, byte[] pngBytes)
    {
        if (runState is not RunState state || _savedData == null || pngBytes.Length == 0)
        {
            return;
        }

        ActivateRun(state);
        string base64 = Convert.ToBase64String(pngBytes);
        _savedData.Modify(state, data =>
        {
            data.Artworks ??= new List<CardArtworkData>();
            CardArtworkData? existing = data.Artworks.FirstOrDefault(item => string.Equals(item.CardId, cardId, StringComparison.Ordinal));
            if (existing == null)
            {
                data.Artworks.Add(new CardArtworkData { CardId = cardId, PngBase64 = base64 });
            }
            else
            {
                existing.PngBase64 = base64;
            }
        });

        Texture2D? texture = DecodeTexture(pngBytes);
        if (texture != null)
        {
            TextureCache[cardId] = texture;
        }
    }

    public static bool TryGetTexture(CardModel card, out Texture2D texture)
    {
        texture = null!;
        RunState? state = card.RunState as RunState ?? RunManager.Instance.DebugOnlyGetState();
        if (state == null || _savedData == null)
        {
            return false;
        }

        ActivateRun(state);
        string cardId = card.Id.Entry;
        if (TextureCache.TryGetValue(cardId, out texture!))
        {
            return true;
        }

        ArtworkRunState data = _savedData.Get(state);
        data.Artworks ??= new List<CardArtworkData>();
        CardArtworkData? artwork = data.Artworks.FirstOrDefault(item => string.Equals(item.CardId, cardId, StringComparison.Ordinal));
        if (artwork == null || string.IsNullOrWhiteSpace(artwork.PngBase64))
        {
            return false;
        }

        try
        {
            Texture2D? decoded = DecodeTexture(Convert.FromBase64String(artwork.PngBase64));
            if (decoded == null)
            {
                return false;
            }

            texture = decoded;
            TextureCache[cardId] = decoded;
            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to decode saved artwork for {cardId}: {ex.Message}");
            return false;
        }
    }

    private static Texture2D? DecodeTexture(byte[] pngBytes)
    {
        Image image = new();
        Error error = image.LoadPngFromBuffer(pngBytes);
        return error == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
    }

    private static byte[] AdaptToCardPortrait(CardModel card, byte[] pngBytes)
    {
        try
        {
            Image source = new();
            if (source.LoadPngFromBuffer(pngBytes) != Error.Ok || source.IsEmpty())
            {
                return pngBytes;
            }

            int targetWidth = card.Rarity == CardRarity.Ancient
                ? DrawingCanvas.AncientCanvasWidth
                : DrawingCanvas.StandardCanvasWidth;
            int targetHeight = card.Rarity == CardRarity.Ancient
                ? DrawingCanvas.AncientCanvasHeight
                : DrawingCanvas.StandardCanvasHeight;
            if (source.GetWidth() == targetWidth && source.GetHeight() == targetHeight)
            {
                return pngBytes;
            }

            source.Convert(Image.Format.Rgba8);
            int sourceWidth = source.GetWidth();
            int sourceHeight = source.GetHeight();
            source.Resize(targetWidth, targetHeight, Image.Interpolation.Lanczos);
            Entry.Logger.Debug(
                $"[DrawAndGuessMod] Adapted artwork for {card.Id.Entry}: " +
                $"{sourceWidth}x{sourceHeight} -> {targetWidth}x{targetHeight}.");
            return source.SavePngToBuffer();
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Failed to adapt artwork for {card.Id.Entry}; using the original PNG: {ex.Message}");
            return pngBytes;
        }
    }
}
