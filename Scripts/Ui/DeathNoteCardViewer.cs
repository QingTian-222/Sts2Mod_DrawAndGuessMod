using System.Collections.Generic;
using System.Linq;
using DrawAndGuessMod.Scripts.Relics;
using DrawAndGuessMod.Scripts.State;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Saves;

namespace DrawAndGuessMod.Scripts.Ui;

internal static class DeathNoteCardViewer
{
    public const string InfoTextKey = "DRAW_AND_GUESS_MOD_DEATH_NOTE_ERASED_CARDS.infoText";

    public static void Show(DeathNote relic)
    {
        IReadOnlyList<ModelId> erasedCardIds = ErasedCardStore.GetErasedCardIds(relic.Owner.RunState);
        List<CardPileAddResult> cards = erasedCardIds
            .Select(cardId => new CardPileAddResult
            {
                success = true,
                cardAdded = SaveUtil.CardOrDeprecated(cardId)
            })
            .ToList();
        if (cards.Count == 0)
        {
            return;
        }

        NSimpleCardsViewScreen.ShowScreen(
            cards,
            new LocString("relics", InfoTextKey));
    }
}
