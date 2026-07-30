using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.RunData;

namespace DrawAndGuessMod.Scripts.State;

internal static class ErasedCardStore
{
    private static RunSavedData<ErasedCardRunState>? _savedData;

    public static void Register()
    {
        _savedData = RitsuLibFramework.GetRunSavedDataStore(Entry.ModId).Register(
            "erased_cards",
            () => new ErasedCardRunState(),
            new RunSavedDataOptions
            {
                SchemaVersion = 1,
                WritePolicy = RunSavedDataWritePolicy.WhenNonDefault
            });
    }

    public static bool IsErased(IRunState runState, ModelId cardId)
    {
        if (runState is not RunState state || _savedData == null)
        {
            return false;
        }

        ErasedCardRunState data = _savedData.Get(state);
        data.CardIds ??= new List<string>();
        return data.CardIds.Contains(cardId.ToString(), StringComparer.Ordinal);
    }

    public static bool Erase(RunState runState, ModelId cardId)
    {
        if (_savedData == null)
        {
            return false;
        }

        string cardKey = cardId.ToString();
        bool added = false;
        _savedData.Modify(runState, data =>
        {
            data.CardIds ??= new List<string>();
            if (data.CardIds.Contains(cardKey, StringComparer.Ordinal))
            {
                return;
            }

            data.CardIds.Add(cardKey);
            added = true;
        });
        return added;
    }

    public static IReadOnlyList<ModelId> GetErasedCardIds(IRunState runState)
    {
        if (runState is not RunState state || _savedData == null)
        {
            return [];
        }

        ErasedCardRunState data = _savedData.Get(state);
        data.CardIds ??= new List<string>();
        List<ModelId> result = new(data.CardIds.Count);
        foreach (string cardKey in data.CardIds)
        {
            try
            {
                result.Add(ModelId.Deserialize(cardKey));
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn(
                    $"[DrawAndGuessMod] Ignored invalid erased card id '{cardKey}': {ex.Message}");
            }
        }
        return result;
    }

    public static async Task RemoveExistingCardsAsync(RunState runState, ModelId cardId)
    {
        List<CardModel> deckCards = runState.Players
            .SelectMany(player => PileType.Deck.GetPile(player).Cards)
            .Where(card => card.Id == cardId)
            .ToList();
        if (deckCards.Count > 0)
        {
            await CardPileCmd.RemoveFromDeck(deckCards, showPreview: true);
        }

        if (!CombatManager.Instance.IsInProgress)
        {
            return;
        }

        List<CardModel> combatCards = new();
        foreach (var player in runState.Players)
        {
            foreach (PileType pileType in Enum.GetValues<PileType>().Where(type => type.IsCombatPile()))
            {
                combatCards.AddRange(pileType.GetPile(player).Cards.Where(card =>
                    card.Id == cardId));
            }
        }

        if (combatCards.Count > 0)
        {
            await CardPileCmd.RemoveFromCombat(combatCards);
        }
    }
}
