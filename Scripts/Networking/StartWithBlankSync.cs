using System.Collections.Generic;
using System.Reflection;
using DrawAndGuessMod.Scripts.Cards;
using DrawAndGuessMod.Scripts.Config;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Networking;

public sealed class StartWithBlankPreferenceMessage : INetMessage, IPacketSerializable
{
    public ulong PlayerId { get; set; }
    public bool Enabled { get; set; }

    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(PlayerId);
        writer.WriteBool(Enabled);
    }

    public void Deserialize(PacketReader reader)
    {
        PlayerId = reader.ReadULong();
        Enabled = reader.ReadBool();
    }

    public override string ToString()
    {
        return $"StartWithBlankPreferenceMessage player={PlayerId} enabled={Enabled}";
    }
}

internal static class StartWithBlankSync
{
    private static readonly Dictionary<ulong, bool> Preferences = new();
    private static INetGameService? _registeredService;

    public static void Attach(INetGameService netService)
    {
        if (ReferenceEquals(_registeredService, netService))
        {
            return;
        }

        Detach();
        Preferences.Clear();
        netService.RegisterMessageHandler<StartWithBlankPreferenceMessage>(OnPreferenceReceived);
        _registeredService = netService;
    }

    public static void Detach()
    {
        if (_registeredService != null)
        {
            _registeredService.UnregisterMessageHandler<StartWithBlankPreferenceMessage>(OnPreferenceReceived);
            _registeredService = null;
        }
    }

    public static void Publish(StartRunLobby lobby)
    {
        Attach(lobby.NetService);
        ulong playerId = lobby.NetService.NetId;
        bool enabled = DrawAndGuessSettings.GainBlankAtRunStart;
        Preferences[playerId] = enabled;
        lobby.NetService.SendMessage(new StartWithBlankPreferenceMessage
        {
            PlayerId = playerId,
            Enabled = enabled
        });
    }

    public static void AddStartingCards(IReadOnlyList<Player> players)
    {
        if (Preferences.Count == 0 && players.Count == 1)
        {
            Preferences[players[0].NetId] = DrawAndGuessSettings.GainBlankAtRunStart;
        }

        foreach (Player player in players)
        {
            if (!Preferences.TryGetValue(player.NetId, out bool enabled) || !enabled)
            {
                continue;
            }

            CardModel blank = ModelDb.Card<Blank>().ToMutable();
            blank.FloorAddedToDeck = 1;
            player.Deck.AddInternal(blank);
        }

        Preferences.Clear();
    }

    private static void OnPreferenceReceived(StartWithBlankPreferenceMessage message, ulong senderId)
    {
        Preferences[message.PlayerId] = message.Enabled;
        Entry.Logger.Debug(
            $"[DrawAndGuessMod] Received start-with-Blank preference for {message.PlayerId} " +
            $"from {senderId}: {message.Enabled}.");
    }
}

[HarmonyPatch]
internal static class StartRunLobbyConstructionPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        return AccessTools.GetDeclaredConstructors(typeof(StartRunLobby));
    }

    [HarmonyPostfix]
    private static void Postfix(StartRunLobby __instance)
    {
        StartWithBlankSync.Attach(__instance.NetService);
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.SetReady))]
internal static class StartRunLobbyReadyPatch
{
    [HarmonyPrefix]
    private static void Prefix(StartRunLobby __instance)
    {
        StartWithBlankSync.Publish(__instance);
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
internal static class StartRunLobbyCleanupPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        StartWithBlankSync.Detach();
    }
}

[HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
internal static class AddBlankToStartingDeckPatch
{
    [HarmonyPrefix]
    private static void Prefix(IReadOnlyList<Player> players)
    {
        StartWithBlankSync.AddStartingCards(players);
    }
}
