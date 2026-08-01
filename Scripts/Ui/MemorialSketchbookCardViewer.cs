using System;
using System.Collections.Generic;
using DrawAndGuessMod.Scripts.Patches;
using DrawAndGuessMod.Scripts.Relics;
using DrawAndGuessMod.Scripts.State;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace DrawAndGuessMod.Scripts.Ui;

internal static class MemorialSketchbookCardViewer
{
    public const string InfoTextKey = "DRAW_AND_GUESS_MOD_MEMORIAL_SKETCHBOOK_CARDS.infoText";
    public static bool HasArtworks(MemorialSketchbook relic)
    {
        return ResolveArtworks(relic).Count > 0;
    }

    public static void Show(MemorialSketchbook relic)
    {
        IReadOnlyList<MemorialArtworkData> artworks = ResolveArtworks(relic);
        List<CardPileAddResult> cards = new(artworks.Count);
        foreach (MemorialArtworkData artwork in artworks)
        {
            try
            {
                // Every page needs its own model instance. Registering previews on
                // ModelDb's canonical card would leak the page art into the card
                // library and make duplicate drawings overwrite one another.
                CardModel card = (CardModel)SaveUtil
                    .CardOrDeprecated(ModelId.Deserialize(artwork.CardId))
                    .MutableClone();
                if (!MemorialArtworkPreviewRegistry.Register(card, artwork))
                {
                    continue;
                }

                cards.Add(new CardPileAddResult
                {
                    success = true,
                    cardAdded = card
                });
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn(
                    $"[DrawAndGuessMod] Ignored invalid Memorial Sketchbook page " +
                    $"'{artwork.ArtworkId}': {ex.Message}");
            }
        }
        if (cards.Count == 0)
        {
            return;
        }

        NSimpleCardsViewScreen.ShowScreen(
            cards,
            new LocString("relics", InfoTextKey));
    }

    private static IReadOnlyList<MemorialArtworkData> ResolveArtworks(MemorialSketchbook relic)
    {
        RunState? activeRun = RunManager.Instance.DebugOnlyGetState();
        if (activeRun != null && ReferenceEquals(relic.Owner.RunState, activeRun))
        {
            return MemorialSketchbookStore.GetCurrentRunArtworks(activeRun);
        }

        return MemorialRunHistoryContext.TryGet(out string seed, out long startTime)
            ? MemorialSketchbookStore.GetArchivedArtworks(seed, startTime)
            : [];
    }
}
