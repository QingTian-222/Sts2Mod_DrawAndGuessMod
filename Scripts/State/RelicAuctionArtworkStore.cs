using System;
using System.Collections.Generic;
using DrawAndGuessMod.Scripts.Networking;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.State;

internal sealed record RelicAuctionPresentation(
    string WorkTitle,
    ulong ArtistId,
    Texture2D Artwork,
    Texture2D TriggerArtwork);

internal static class RelicAuctionArtworkStore
{
    private static readonly Dictionary<ModelId, RelicAuctionPresentation> Presentations = new();
    private static readonly HashSet<ModelId> AwardedPresentationIds = new();
    [ThreadStatic]
    private static RelicModel? _triggerIconContext;

    public static bool IsPickingActive { get; private set; }

    public static void Reset()
    {
        IsPickingActive = false;
        Presentations.Clear();
        AwardedPresentationIds.Clear();
        _triggerIconContext = null;
    }

    public static void InstallPresentations(IEnumerable<RelicAuctionSubmission> submissions)
    {
        foreach (RelicAuctionSubmission submission in submissions)
        {
            try
            {
                Image image = new();
                if (image.LoadPngFromBuffer(submission.PngBytes) != Error.Ok)
                {
                    continue;
                }

                Texture2D texture = ImageTexture.CreateFromImage(image);
                Image triggerImage = Image.CreateFromData(
                    image.GetWidth(),
                    image.GetHeight(),
                    false,
                    image.GetFormat(),
                    image.GetData());
                triggerImage.Resize(
                    68,
                    68,
                    Image.Interpolation.Lanczos);
                Texture2D triggerTexture =
                    ImageTexture.CreateFromImage(triggerImage);
                Presentations[new ModelId("RELIC", submission.TargetRelicId)] =
                    new RelicAuctionPresentation(
                        submission.WorkTitle,
                        submission.OwnerId,
                        texture,
                        triggerTexture);
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn(
                    $"[DrawAndGuessMod] Failed to install relic auction artwork " +
                    $"{submission.TargetRelicId}: {ex.Message}");
            }
        }
    }

    public static bool TryGet(RelicModel relic, out RelicAuctionPresentation presentation)
    {
        return Presentations.TryGetValue(relic.Id, out presentation!);
    }

    public static void MarkAwarded(RelicModel relic)
    {
        if (Presentations.ContainsKey(relic.Id))
        {
            AwardedPresentationIds.Add(relic.Id);
        }
    }

    public static bool TryGetAwarded(
        RelicModel relic,
        out RelicAuctionPresentation presentation)
    {
        if (!AwardedPresentationIds.Contains(relic.Id))
        {
            presentation = null!;
            return false;
        }

        return Presentations.TryGetValue(relic.Id, out presentation!);
    }

    public static RelicModel? PushTriggerIconContext(RelicModel relic)
    {
        RelicModel? previous = _triggerIconContext;
        _triggerIconContext = relic;
        return previous;
    }

    public static void RestoreTriggerIconContext(RelicModel? previous)
    {
        _triggerIconContext = previous;
    }

    public static bool TryGetTriggerArtwork(
        RelicModel relic,
        out Texture2D texture)
    {
        if (_triggerIconContext?.Id != relic.Id ||
            !TryGetAwarded(
                relic,
                out RelicAuctionPresentation? presentation))
        {
            texture = null!;
            return false;
        }

        texture = presentation.TriggerArtwork;
        return true;
    }

    public static void SetPickingActive(bool active)
    {
        IsPickingActive = active;
    }
}
