using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.RunData;

namespace DrawAndGuessMod.Scripts.State;

internal static class BlankSelectionStore
{
    private static RunSavedData<BlankSelectionRunState>? _savedData;

    public static void Register()
    {
        _savedData = RitsuLibFramework.GetRunSavedDataStore(Entry.ModId).Register(
            "blank_selections",
            () => new BlankSelectionRunState(),
            new RunSavedDataOptions
            {
                SchemaVersion = 1,
                WritePolicy = RunSavedDataWritePolicy.WhenNonDefault
            });
    }

    public static void Remember(IRunState runState, ModelId cardId)
    {
        if (runState is not RunState state || _savedData == null)
        {
            return;
        }

        string cardKey = cardId.ToString();
        _savedData.Modify(state, data =>
        {
            data.CardIds ??= new List<string>();
            if (!data.CardIds.Contains(cardKey, StringComparer.Ordinal))
            {
                data.CardIds.Add(cardKey);
            }
        });
    }

    public static HashSet<ModelId> GetSelectedCardIds(IRunState runState)
    {
        HashSet<ModelId> result = new();
        if (runState is not RunState state || _savedData == null)
        {
            return result;
        }

        BlankSelectionRunState data = _savedData.Get(state);
        data.CardIds ??= new List<string>();
        foreach (string cardKey in data.CardIds)
        {
            try
            {
                result.Add(ModelId.Deserialize(cardKey));
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn(
                    $"[DrawAndGuessMod] Ignored invalid Blank selection id '{cardKey}': {ex.Message}");
            }
        }
        return result;
    }
}
