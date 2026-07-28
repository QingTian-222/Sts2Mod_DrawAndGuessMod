using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.RunData;

namespace DrawAndGuessMod.Scripts.State;

internal readonly record struct GalleryChallengeRoll(int Ordinal, IReadOnlyList<string> UsedTargetIds);

internal static class GalleryChallengeStore
{
    private static RunSavedData<GalleryChallengeRunState>? _savedData;

    public static void Register()
    {
        _savedData = RitsuLibFramework.GetRunSavedDataStore(Entry.ModId).Register(
            "gallery_challenges",
            () => new GalleryChallengeRunState(),
            new RunSavedDataOptions
            {
                SchemaVersion = 1,
                WritePolicy = RunSavedDataWritePolicy.WhenNonDefault
            });
    }

    public static GalleryChallengeRoll ReserveRoll(Player player)
    {
        if (player.RunState is not RunState state || _savedData == null)
        {
            return new GalleryChallengeRoll(0, Array.Empty<string>());
        }

        int slot = state.GetPlayerSlotIndex(player);
        GalleryChallengeRoll result = default;
        _savedData.Modify(state, data =>
        {
            data.Players ??= new List<GalleryChallengePlayerState>();
            GalleryChallengePlayerState? playerState = data.Players.FirstOrDefault(item => item.PlayerSlot == slot);
            if (playerState == null)
            {
                playerState = new GalleryChallengePlayerState { PlayerSlot = slot };
                data.Players.Add(playerState);
            }

            playerState.UsedTargetIds ??= new List<string>();
            if (!string.IsNullOrEmpty(playerState.LastTargetId) &&
                !playerState.UsedTargetIds.Contains(playerState.LastTargetId, StringComparer.Ordinal))
            {
                playerState.UsedTargetIds.Add(playerState.LastTargetId);
            }

            result = new GalleryChallengeRoll(playerState.RollsReserved, playerState.UsedTargetIds.ToArray());
            playerState.RollsReserved++;
        });
        return result;
    }

    public static void RememberTarget(Player player, string targetId)
    {
        if (player.RunState is not RunState state || _savedData == null)
        {
            return;
        }

        int slot = state.GetPlayerSlotIndex(player);
        _savedData.Modify(state, data =>
        {
            data.Players ??= new List<GalleryChallengePlayerState>();
            GalleryChallengePlayerState? playerState = data.Players.FirstOrDefault(item => item.PlayerSlot == slot);
            if (playerState == null)
            {
                playerState = new GalleryChallengePlayerState { PlayerSlot = slot };
                data.Players.Add(playerState);
            }
            playerState.UsedTargetIds ??= new List<string>();
            if (!playerState.UsedTargetIds.Contains(targetId, StringComparer.Ordinal))
            {
                playerState.UsedTargetIds.Add(targetId);
            }
            playerState.LastTargetId = targetId;
        });
    }

    public static void SetMemorialCards(Player player, IEnumerable<ModelId> cardIds)
    {
        if (player.RunState is not RunState state || _savedData == null)
        {
            return;
        }

        int slot = state.GetPlayerSlotIndex(player);
        List<string> serializedIds = cardIds
            .Select(cardId => cardId.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        _savedData.Modify(state, data =>
        {
            data.Players ??= new List<GalleryChallengePlayerState>();
            GalleryChallengePlayerState? playerState =
                data.Players.FirstOrDefault(item => item.PlayerSlot == slot);
            if (playerState == null)
            {
                playerState = new GalleryChallengePlayerState { PlayerSlot = slot };
                data.Players.Add(playerState);
            }

            playerState.MemorialCardIds = serializedIds;
        });
    }

    public static IReadOnlyList<ModelId> GetMemorialCardIds(Player player)
    {
        if (player.RunState is not RunState state || _savedData == null)
        {
            return [];
        }

        int slot = state.GetPlayerSlotIndex(player);
        GalleryChallengeRunState data = _savedData.Get(state);
        GalleryChallengePlayerState? playerState =
            data.Players?.FirstOrDefault(item => item.PlayerSlot == slot);
        if (playerState?.MemorialCardIds == null)
        {
            return [];
        }

        List<ModelId> result = new(playerState.MemorialCardIds.Count);
        foreach (string cardKey in playerState.MemorialCardIds)
        {
            try
            {
                result.Add(ModelId.Deserialize(cardKey));
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn(
                    $"[DrawAndGuessMod] Ignored invalid memorial card id '{cardKey}': {ex.Message}");
            }
        }
        return result;
    }

    public static int RemoveMemorialCard(RunState runState, ModelId cardId)
    {
        if (_savedData == null)
        {
            return 0;
        }

        string cardKey = cardId.ToString();
        int removed = 0;
        _savedData.Modify(runState, data =>
        {
            data.Players ??= new List<GalleryChallengePlayerState>();
            foreach (GalleryChallengePlayerState playerState in data.Players)
            {
                playerState.MemorialCardIds ??= new List<string>();
                removed += playerState.MemorialCardIds.RemoveAll(
                    savedId => string.Equals(savedId, cardKey, StringComparison.Ordinal));
            }
        });
        return removed;
    }
}

public sealed class GalleryChallengeRunState
{
    public List<GalleryChallengePlayerState> Players { get; set; } = new();
}

public sealed class GalleryChallengePlayerState
{
    public int PlayerSlot { get; set; }
    public int RollsReserved { get; set; }
    public string? LastTargetId { get; set; }
    public List<string> UsedTargetIds { get; set; } = new();
    public List<string> MemorialCardIds { get; set; } = new();
}
