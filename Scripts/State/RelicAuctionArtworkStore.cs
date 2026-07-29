using System;
using System.Collections.Generic;
using DrawAndGuessMod.Scripts.Networking;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.State;

internal sealed record RelicAuctionPresentation(
    string WorkTitle,
    ulong ArtistId,
    Texture2D Artwork);

internal static class RelicAuctionArtworkStore
{
    private static readonly Dictionary<ModelId, RelicAuctionPresentation> Presentations = new();

    public static bool IsPickingActive { get; private set; }

    public static void Reset()
    {
        IsPickingActive = false;
        Presentations.Clear();
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
                Presentations[new ModelId("RELIC", submission.TargetRelicId)] =
                    new RelicAuctionPresentation(
                        submission.WorkTitle,
                        submission.OwnerId,
                        texture);
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

    public static void SetPickingActive(bool active)
    {
        IsPickingActive = active;
    }
}
