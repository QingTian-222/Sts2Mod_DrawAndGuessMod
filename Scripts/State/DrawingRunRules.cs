using System;
using System.Collections.Generic;
using System.Linq;
using DrawAndGuessMod.Scripts.Cards;
using DrawAndGuessMod.Scripts.Config;
using DrawAndGuessMod.Scripts.Networking;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.RunData;
using STS2RitsuLib.RunRngs;
using STS2RitsuLib.Utils;

namespace DrawAndGuessMod.Scripts.State;

public enum InitialBlankMode
{
    None = 0,
    RandomPlayer = 1,
    AllPlayers = 2
}

public enum DrawingCardRestriction
{
    None = 0,
    ExcludeAncient = 1,
    CurrentCharacter = 2
}

public sealed class DrawingRunRuleState
{
    public bool Configured { get; set; }
    public int InitialBlankMode { get; set; }
    public int DrawingTimeLimitSeconds { get; set; }
    public bool ExcludePreviouslySelectedCards { get; set; }
    public bool BlankGeneratedCardSkipsDeck { get; set; }
    public int CardRestriction { get; set; }
    public List<ulong> InitialBlankRecipients { get; set; } = new();
}

internal static class DrawingRunRules
{
    private const string SavedDataKey = "drawing_run_rules";
    private const string InitialBlankRngKey = "initial_blank_recipient";
    private static readonly int[] AllowedDrawingTimes = [0, 30, 60, 90, 120];
    private static readonly SavedAttachedState<CardModel, bool> GrantedInitialBlank =
        new("DrawAndGuessModInitialBlank", _ => false);
    private static RunSavedData<DrawingRunRuleState>? _savedData;

    public static event Action<RunState>? RulesChanged;

    public static void Register()
    {
        _savedData = RitsuLibFramework.GetRunSavedDataStore(Entry.ModId).Register(
            SavedDataKey,
            () => new DrawingRunRuleState(),
            new RunSavedDataOptions
            {
                SchemaVersion = 1,
                WritePolicy = RunSavedDataWritePolicy.WhenNonDefault
            });
    }

    public static void ActivateRun(RunState runState)
    {
        DrawingRunRulesSync.Attach(RunManager.Instance.NetService);
        if (IsConfigured(runState))
        {
            ReconcileInitialBlanks(runState, GetSnapshot(runState).InitialBlankRecipients);
        }
    }

    public static bool IsConfigured(RunState runState)
    {
        return _savedData?.Get(runState).Configured == true;
    }

    public static DrawingRunRuleState GetSnapshot(RunState runState)
    {
        if (_savedData != null)
        {
            DrawingRunRuleState saved = _savedData.Get(runState);
            if (saved.Configured)
            {
                return Sanitize(saved);
            }
        }

        return CreateDefaults();
    }

    public static double? GetDrawingTimeLimitSeconds(IRunState runState)
    {
        if (runState is RunState state && IsConfigured(state))
        {
            int seconds = GetSnapshot(state).DrawingTimeLimitSeconds;
            return seconds == 0 ? null : seconds;
        }

        return DrawAndGuessSettings.DrawingTimeLimitSeconds;
    }

    public static bool GetExcludePreviouslySelectedCards(IRunState runState)
    {
        return runState is RunState state && IsConfigured(state)
            ? GetSnapshot(state).ExcludePreviouslySelectedCards
            : DrawAndGuessSettings.ExcludePreviouslySelectedBlankCards;
    }

    public static bool GetBlankGeneratedCardSkipsDeck(IRunState runState)
    {
        return runState is RunState state && IsConfigured(state)
            ? GetSnapshot(state).BlankGeneratedCardSkipsDeck
            : DrawAndGuessSettings.BlankGeneratedCardSkipsDeck;
    }

    public static DrawingCardRestriction GetCardRestriction(IRunState runState)
    {
        if (runState is RunState state && IsConfigured(state))
        {
            return (DrawingCardRestriction)GetSnapshot(state).CardRestriction;
        }

        return DrawAndGuessSettings.CardPoolScope == GuessCardPoolScope.CurrentCharacter
            ? DrawingCardRestriction.CurrentCharacter
            : DrawingCardRestriction.None;
    }

    public static DrawingRunRuleState ApplyHostSettings(
        RunState runState,
        InitialBlankMode initialBlankMode,
        int drawingTimeLimitSeconds,
        bool excludePreviouslySelectedCards,
        bool blankGeneratedCardSkipsDeck,
        DrawingCardRestriction cardRestriction)
    {
        DrawingRunRuleState previous = GetSnapshot(runState);
        List<ulong> recipients = ResolveRecipients(runState, initialBlankMode, previous);
        DrawingRunRuleState next = new()
        {
            Configured = true,
            InitialBlankMode = (int)initialBlankMode,
            DrawingTimeLimitSeconds = NormalizeDrawingTime(drawingTimeLimitSeconds),
            ExcludePreviouslySelectedCards = excludePreviouslySelectedCards,
            BlankGeneratedCardSkipsDeck = blankGeneratedCardSkipsDeck,
            CardRestriction = (int)cardRestriction,
            InitialBlankRecipients = recipients
        };
        ApplyState(runState, next);
        DrawingRunRulesSync.Publish(next);
        return next;
    }

    public static DrawingRunRuleState ApplyHostDefaults(RunState runState)
    {
        DrawingRunRuleState defaults = CreateDefaults();
        return ApplyHostSettings(
            runState,
            (InitialBlankMode)defaults.InitialBlankMode,
            defaults.DrawingTimeLimitSeconds,
            defaults.ExcludePreviouslySelectedCards,
            defaults.BlankGeneratedCardSkipsDeck,
            (DrawingCardRestriction)defaults.CardRestriction);
    }

    public static void ApplySyncedSettings(RunState runState, DrawingRunRuleState state)
    {
        ApplyState(runState, Sanitize(state));
    }

    private static void ApplyState(RunState runState, DrawingRunRuleState state)
    {
        if (_savedData == null)
        {
            return;
        }

        DrawingRunRuleState snapshot = Sanitize(state);
        _savedData.Set(runState, snapshot);
        ReconcileInitialBlanks(runState, snapshot.InitialBlankRecipients);
        RulesChanged?.Invoke(runState);
        Entry.Logger.Info(
            $"[DrawAndGuessMod] Applied run drawing rules: blankMode={(InitialBlankMode)snapshot.InitialBlankMode}, " +
            $"recipients={string.Join(',', snapshot.InitialBlankRecipients)}, time={snapshot.DrawingTimeLimitSeconds}, " +
            $"noRepeat={snapshot.ExcludePreviouslySelectedCards}, skipDeck={snapshot.BlankGeneratedCardSkipsDeck}, " +
            $"restriction={(DrawingCardRestriction)snapshot.CardRestriction}.");
    }

    private static List<ulong> ResolveRecipients(RunState runState, InitialBlankMode mode, DrawingRunRuleState previous)
    {
        IReadOnlyList<Player> players = runState.Players;
        if (mode == InitialBlankMode.None || players.Count == 0)
        {
            return new List<ulong>();
        }
        if (mode == InitialBlankMode.AllPlayers)
        {
            return players.Select(player => player.NetId).ToList();
        }

        ulong previousRecipient = previous.InitialBlankRecipients.FirstOrDefault();
        if ((InitialBlankMode)previous.InitialBlankMode == InitialBlankMode.RandomPlayer &&
            players.Any(player => player.NetId == previousRecipient))
        {
            return new List<ulong> { previousRecipient };
        }

        int index = ModRunRngRegistry.Get(runState, Entry.ModId, InitialBlankRngKey).NextInt(players.Count);
        return new List<ulong> { players[index].NetId };
    }

    private static void ReconcileInitialBlanks(RunState runState, IReadOnlyCollection<ulong> recipientIds)
    {
        HashSet<ulong> desiredRecipients = recipientIds
            .Where(id => runState.Players.Any(player => player.NetId == id))
            .ToHashSet();

        foreach (Player player in runState.Players)
        {
            List<CardModel> grantedCards = player.Deck.Cards
                .Where(card => GrantedInitialBlank.GetValueOrDefault(card))
                .ToList();
            int keepCount = desiredRecipients.Contains(player.NetId) ? 1 : 0;
            foreach (CardModel card in grantedCards.Skip(keepCount).ToList())
            {
                player.Deck.RemoveInternal(card);
                runState.RemoveCard(card);
            }

            if (keepCount == 1 && grantedCards.Count == 0)
            {
                Blank blank = runState.CreateCard<Blank>(player);
                blank.FloorAddedToDeck = Math.Max(1, runState.TotalFloor);
                GrantedInitialBlank[blank] = true;
                player.Deck.AddInternal(blank);
            }
        }
    }

    private static DrawingRunRuleState CreateDefaults()
    {
        double? configuredTime = DrawAndGuessSettings.DrawingTimeLimitSeconds;
        return new DrawingRunRuleState
        {
            Configured = false,
            InitialBlankMode = (int)InitialBlankMode.None,
            DrawingTimeLimitSeconds = NormalizeDrawingTime(configuredTime.HasValue ? (int)Math.Round(configuredTime.Value) : 0),
            ExcludePreviouslySelectedCards = DrawAndGuessSettings.ExcludePreviouslySelectedBlankCards,
            BlankGeneratedCardSkipsDeck = DrawAndGuessSettings.BlankGeneratedCardSkipsDeck,
            CardRestriction = DrawAndGuessSettings.CardPoolScope == GuessCardPoolScope.CurrentCharacter
                ? (int)DrawingCardRestriction.CurrentCharacter
                : (int)DrawingCardRestriction.None
        };
    }

    private static DrawingRunRuleState Sanitize(DrawingRunRuleState source)
    {
        return new DrawingRunRuleState
        {
            Configured = source.Configured,
            InitialBlankMode = Math.Clamp(source.InitialBlankMode, 0, 2),
            DrawingTimeLimitSeconds = NormalizeDrawingTime(source.DrawingTimeLimitSeconds),
            ExcludePreviouslySelectedCards = source.ExcludePreviouslySelectedCards,
            BlankGeneratedCardSkipsDeck = source.BlankGeneratedCardSkipsDeck,
            CardRestriction = Math.Clamp(source.CardRestriction, 0, 2),
            InitialBlankRecipients = source.InitialBlankRecipients?.Distinct().Take(8).ToList() ?? new List<ulong>()
        };
    }

    private static int NormalizeDrawingTime(int seconds)
    {
        return AllowedDrawingTimes.Contains(seconds)
            ? seconds
            : AllowedDrawingTimes.OrderBy(candidate => Math.Abs(candidate - seconds)).First();
    }
}
