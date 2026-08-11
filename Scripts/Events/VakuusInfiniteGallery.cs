using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Ai;
using DrawAndGuessMod.Scripts.Assets;
using DrawAndGuessMod.Scripts.Cards;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.Networking;
using DrawAndGuessMod.Scripts.Relics;
using DrawAndGuessMod.Scripts.State;
using DrawAndGuessMod.Scripts.Ui;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace DrawAndGuessMod.Scripts.Events;

[RegisterSharedEvent]
public sealed class VakuusInfiniteGallery : ModEventTemplate
{
    private const int TimedReward = 60;
    private const int StandardFirstReward = 60;
    private const int TimedGalleryRewardWins = 3;
    private const double TimedChallengeSeconds = 60d;
    private ChallengeMode _mode;
    private int _challengeNumber;
    private int _timedSuccessCount;
    private List<CardModel> _timedSuccessfulCards = new();
    private bool _standardRewardClaimed;
    private bool _timedGalleryRewardClaimed;

    public override bool IsShared => true;
    public override bool IsAllowed(IRunState runState)
    {
        return DrawingRunRules.IsGameplayEnabled(runState);
    }

    public override EventAssetProfile AssetProfile =>
        new(null!, DrawAndGuessAssets.GalleryPortraitPath, null!, null!);
    private Player EventOwner => Owner ?? throw new InvalidOperationException("The gallery event has no owner.");
    private Player HostPlayer => EventOwner.RunState.Players.First(
        player => player.NetId == DrawingNetSync.HostNetId);

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new StringVar("Target"),
        new StringVar("Chosen"),
        new StringVar("Reaction"),
        new GoldVar("Reward", 0),
        new IntVar("SuccessCount", 0)
    ];

    protected override Task BeforeEventStarted(bool isPreFinished)
    {
        _timedSuccessfulCards = new List<CardModel>();
        // This event can be owned by a guest. Every peer must advance its local
        // event epoch so history-session ids cannot repeat on guest-owned runs.
        DrawingNetSync.BeginGalleryEvent();
        return Task.CompletedTask;
    }

    protected override IReadOnlyList<EventOption> GenerateInitialOptions()
    {
        return
        [
            new EventOption(this, StartTimedChallenge, OptionKey("INITIAL", "TIMED")),
            new EventOption(this, StartStandardChallenge, OptionKey("INITIAL", "STANDARD"))
        ];
    }

    private async Task StartTimedChallenge()
    {
        _mode = ChallengeMode.Timed;
        await RunChallenge();
    }

    private async Task StartStandardChallenge()
    {
        _mode = ChallengeMode.Standard;
        await RunChallenge();
    }

    private async Task RunChallenge()
    {
        uint sessionId = CreateSessionId();
        CardModel? target = await GetSynchronizedTarget(sessionId);
        if (target == null)
        {
            Entry.Logger.Info(
                $"[DrawAndGuessMod] Gallery exhausted for {EventOwner.NetId} after {_challengeNumber} challenges.");
            bool claimedReward = await ClaimTimedGalleryRewardForEventOwnerIfEligible();
            SetEventFinished(PageDescription(claimedReward ? "EXHAUSTED_REWARD" : "EXHAUSTED"));
            return;
        }

        string targetDisplayName = GetChallengeDisplayName(target);
        SetStringVar("Target", targetDisplayName);
        SetStringVar("Chosen", ModText.Get("DRAW_AND_GUESS_MOD.RELIC_APPRAISAL_FAIR.NONE"));
        DynamicVars["Reward"].BaseValue = 0;

        _challengeNumber++;
        Entry.Logger.Info(
            $"[DrawAndGuessMod] Gallery challenge {_challengeNumber} started for {EventOwner.NetId}: " +
            $"mode={_mode}, target={target.Id.Entry}, session={sessionId}.");
        DrawingScreenOptions screenOptions = new(
            ModText.Format(
                "DRAW_AND_GUESS_MOD.VAKUUS_INFINITE_GALLERY.TARGET",
                ("Target", targetDisplayName)),
            _mode == ChallengeMode.Timed
                ? ModText.Get("DRAW_AND_GUESS_MOD.VAKUUS_INFINITE_GALLERY.FINISH_BEFORE_TIME_RUNS_OUT_THEN_CHOOSE")
                : ModText.Get("DRAW_AND_GUESS_MOD.VAKUUS_INFINITE_GALLERY.FINISH_THE_DRAWING_THEN_CHOOSE_THE_TARGET"),
            _mode == ChallengeMode.Timed ? TimedChallengeSeconds : null,
            ModText.Get("DRAW_AND_GUESS_MOD.RELIC_APPRAISAL_FAIR.VIEW_EVENT"));

        screenOptions = screenOptions with
        {
            InitialCanvasMode = target.Rarity == CardRarity.Ancient
                ? DrawingCanvasMode.Ancient
                : DrawingCanvasMode.Standard,
            AllowCanvasModeSwitch = false
        };
        (DrawingResult? drawing, ulong drawerNetId) = DrawingNetSync.IsMultiplayer
            ? await DrawAndVoteAsync(sessionId, screenOptions)
            : (await DrawingScreen.ShowAsync(HostPlayer, sessionId, screenOptions), HostPlayer.NetId);
        if (drawing == null)
        {
            ResolveFailure();
            return;
        }

        RunState runState = (RunState)EventOwner.RunState;
        string? memorialArtworkId = await MemorialSketchbookStore.CaptureCardDrawingAsync(
            runState,
            drawerNetId,
            sessionId,
            drawing);

        List<CardModel> choices = drawing.Guess.NearestCards
            .Take(3)
            .Select(candidate => EventOwner.RunState.CreateCard(candidate, HostPlayer))
            .ToList();
        if (choices.Count == 0)
        {
            ResolveFailure();
            return;
        }

        CardModel? selected = await SelectGuessAsync(sessionId, choices);
        if (selected == null)
        {
            ResolveFailure();
            return;
        }

        SetStringVar("Chosen", selected.Title);
        MemorialSketchbookStore.AssignCard(
            runState,
            memorialArtworkId,
            selected,
            drawing.PngBytes);
        ArtworkStore.Set(EventOwner.RunState, selected, drawing.PngBytes);
        DrawingHistoryStore.RecordCard(
            EventOwner.RunState,
            drawerNetId,
            sessionId,
            selected,
            drawing.PngBytes);
        if (selected.Id == target.Id)
        {
            await ResolveSuccess(target);
            return;
        }

        Entry.Logger.Info(
            $"[DrawAndGuessMod] Gallery challenge {_challengeNumber} failed for {EventOwner.NetId}: " +
            $"target={target.Id.Entry}, selected={selected.Id.Entry}.");
        ResolveFailure();
    }

    private async Task<(DrawingResult? Drawing, ulong DrawerNetId)> DrawAndVoteAsync(
        uint sessionId,
        DrawingScreenOptions screenOptions)
    {
        if (LocalContext.IsMe(EventOwner))
        {
            DrawingResult? localDrawing = await DrawingScreen.ShowPrivateAsync(
                EventOwner,
                CreatePrivateDrawingSessionId(sessionId, EventOwner.NetId),
                screenOptions);
            GalleryDrawingSubmission localSubmission = new(
                EventOwner.NetId,
                localDrawing?.PngBytes ?? [],
                localDrawing?.Guess.NearestCards
                    .Take(3)
                    .Select(card => card.Id.Entry)
                    .ToList() ?? []);
            DrawingNetSync.PublishGallerySubmission(sessionId, localSubmission);
        }

        GalleryDrawingSubmission[] submissions = await Task.WhenAll(
            EventOwner.RunState.Players
                .OrderBy(player => player.NetId)
                .Select(player => DrawingNetSync.WaitForGallerySubmissionAsync(
                    sessionId,
                    player.NetId)));
        ulong winnerId = await GalleryDrawingVoteScreen.RunAsync(
            sessionId,
            (RunState)EventOwner.RunState,
            submissions);
        GalleryDrawingSubmission? winner = submissions.FirstOrDefault(
            submission => submission.OwnerId == winnerId);
        if (winner == null || winner.PngBytes.Length == 0)
        {
            return (null, 0ul);
        }

        List<CardModel> nearestCards = winner.CardIds
            .Select(cardId => ModelDb.AllCards.FirstOrDefault(card =>
                string.Equals(card.Id.Entry, cardId, StringComparison.Ordinal)))
            .OfType<CardModel>()
            .ToList();
        if (nearestCards.Count == 0)
        {
            return (null, winner.OwnerId);
        }

        CardGuess guess = new(
            nearestCards[0],
            0,
            0d,
            nearestCards);
        return (new DrawingResult(winner.PngBytes, guess, false), winner.OwnerId);
    }

    private async Task<CardModel?> SelectGuessAsync(uint sessionId, IReadOnlyList<CardModel> choices)
    {
        if (!DrawingNetSync.IsMultiplayer)
        {
            return await CardSelectCmd.FromChooseACardScreen(
                new BlockingPlayerChoiceContext(),
                choices,
                HostPlayer);
        }

        if (EventOwner.NetId == HostPlayer.NetId)
        {
            CardModel? selected = await CardSelectCmd.FromChooseACardScreen(
                new BlockingPlayerChoiceContext(),
                choices,
                HostPlayer);
            DrawingNetSync.CompleteCardSelection(sessionId, selected?.Id.Entry ?? "");
            return selected;
        }

        string selectedCardId = await DrawingNetSync.WaitForCardSelectionAsync(sessionId);
        return choices.FirstOrDefault(card =>
            string.Equals(card.Id.Entry, selectedCardId, StringComparison.Ordinal));
    }

    private async Task<CardModel?> GetSynchronizedTarget(uint sessionId)
    {
        if (!DrawingNetSync.IsMultiplayer)
        {
            return RollTarget();
        }

        bool isCoordinatorEvent = EventOwner.NetId == HostPlayer.NetId;
        if (isCoordinatorEvent && DrawingNetSync.IsLocalHost)
        {
            CardModel? target = RollTarget();
            DrawingNetSync.PublishChallengeTarget(
                HostPlayer.NetId,
                sessionId,
                target?.Id.Entry ?? "");
            return target;
        }

        string targetCardId = await DrawingNetSync.WaitForChallengeTargetAsync(
            HostPlayer.NetId,
            sessionId);
        if (isCoordinatorEvent)
        {
            GalleryChallengeStore.ReserveRoll(HostPlayer);
            if (!string.IsNullOrEmpty(targetCardId))
            {
                GalleryChallengeStore.RememberTarget(HostPlayer, targetCardId);
            }
        }

        if (string.IsNullOrEmpty(targetCardId))
        {
            return null;
        }

        return ModelDb.AllCards.FirstOrDefault(card =>
                   string.Equals(card.Id.Entry, targetCardId, StringComparison.Ordinal))
               ?? throw new InvalidOperationException(
                   $"The synchronized gallery target '{targetCardId}' is not installed on this peer.");
    }

    private async Task ResolveSuccess(CardModel target)
    {
        if (_mode == ChallengeMode.Timed)
        {
            _timedSuccessCount++;
            _timedSuccessfulCards.Add(target);
            DynamicVars["SuccessCount"].BaseValue = _timedSuccessCount;
            SetStringVar("Reaction", GetTimedSuccessReaction());
        }

        int reward = _mode switch
        {
            ChallengeMode.Timed => TimedReward,
            ChallengeMode.Standard when !_standardRewardClaimed => StandardFirstReward,
            _ => 0
        };
        if (_mode == ChallengeMode.Standard && reward > 0)
        {
            _standardRewardClaimed = true;
        }
        if (reward > 0)
        {
            DynamicVars["Reward"].BaseValue = reward;
            await GrantChallengeGoldToEventOwner(reward);
        }

        Entry.Logger.Info(
            $"[DrawAndGuessMod] Gallery challenge {_challengeNumber} succeeded for {EventOwner.NetId}: " +
            $"mode={_mode}, reward={reward}, timedSuccesses={_timedSuccessCount}.");
        string page = _mode == ChallengeMode.Timed
            ? "SUCCESS_TIMED"
            : reward > 0 ? "SUCCESS_REWARD" : "SUCCESS";
        SetEventState(PageDescription(page), SuccessOptions());
    }

    private string GetTimedSuccessReaction()
    {
        return _timedSuccessCount switch
        {
            1 => ModText.Get("DRAW_AND_GUESS_MOD.VAKUUS_INFINITE_GALLERY.THE_CORNER_OF_VAKUU_S_MOUTH_LIFTS"),
            2 => ModText.Get("DRAW_AND_GUESS_MOD.VAKUUS_INFINITE_GALLERY.VAKUU_IS_BEGINNING_TO_LOOK_EXPECTANT"),
            _ => ModText.Get("DRAW_AND_GUESS_MOD.VAKUUS_INFINITE_GALLERY.VAKUU_IS_VERY_PLEASED")
        };
    }

    private void ResolveFailure()
    {
        bool mayContinue = _mode == ChallengeMode.Standard;
        string page = mayContinue
            ? "FAIL_STANDARD"
            : CanClaimTimedGalleryReward ? "FAIL_TIMED_REWARD" : "FAIL_TIMED";
        SetEventState(PageDescription(page), ResultOptions(mayContinue));
    }

    private IReadOnlyList<EventOption> SuccessOptions()
    {
        if (_mode == ChallengeMode.Timed)
        {
            return
            [
                new EventOption(this, RunChallenge, OptionKey("RESULT", "CONTINUE"))
            ];
        }

        return ResultOptions(includeContinue: true);
    }

    private IReadOnlyList<EventOption> ResultOptions(bool includeContinue)
    {
        List<EventOption> options = new(2);
        if (includeContinue)
        {
            options.Add(new EventOption(this, RunChallenge, OptionKey("RESULT", "CONTINUE")));
        }
        string leaveOption = CanClaimTimedGalleryReward ? "TAKE_AND_LEAVE" : "LEAVE";
        options.Add(new EventOption(this, Leave, OptionKey("RESULT", leaveOption)));
        return options;
    }

    private async Task Leave()
    {
        bool claimedReward = await ClaimTimedGalleryRewardForEventOwnerIfEligible();
        SetEventFinished(PageDescription(claimedReward ? "TAKE_REWARD" : "LEAVE"));
    }

    private async Task GrantChallengeGoldToEventOwner(int reward)
    {
        // Shared event options are executed once for every player-owned event instance.
        // Rewarding only this instance's owner grants the reward to the whole party exactly once per player.
        await PlayerCmd.GainGold(reward, EventOwner);
    }

    private bool CanClaimTimedGalleryReward =>
        _mode == ChallengeMode.Timed &&
        _timedSuccessCount >= TimedGalleryRewardWins &&
        _timedSuccessfulCards.Count >= TimedGalleryRewardWins &&
        !_timedGalleryRewardClaimed;

    private async Task<bool> ClaimTimedGalleryRewardForEventOwnerIfEligible()
    {
        if (!CanClaimTimedGalleryReward)
        {
            return false;
        }

        // Every player owns a separate instance of this shared event. Opening the native selector for
        // EventOwner lets each player choose independently while PlayerChoiceSynchronizer relays remote choices.
        Player rewardOwner = EventOwner;
        List<CardModel> rewardCards = _timedSuccessfulCards
            .GroupBy(card => card.Id)
            .Select(group => rewardOwner.RunState.CreateCard(
                group.First(),
                rewardOwner))
            .ToList();
        List<CardCreationResult> rewardOptions = rewardCards
            .Select(card => new CardCreationResult(card))
            .ToList();
        CardSelectorPrefs prefs = new(
            L10NLookup($"{Id.Entry}.selectionScreenPrompt"),
            0,
            1);
        CardModel? selected = (await CardSelectCmd.FromSimpleGridForRewards(
                new BlockingPlayerChoiceContext(),
                rewardOptions,
                rewardOwner,
                prefs))
            .FirstOrDefault();
        _timedGalleryRewardClaimed = true;
        if (selected == null)
        {
            RemoveUnusedRewardCards(rewardCards, null);
            Entry.Logger.Info(
                $"[DrawAndGuessMod] Timed gallery reward skipped by {rewardOwner.NetId}.");
            return false;
        }

        CardPileAddResult addResult = await CardPileCmd.Add(
            selected,
            PileType.Deck);
        if (!addResult.success)
        {
            RemoveUnusedRewardCards(rewardCards, null);
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Failed to add timed gallery reward " +
                $"{selected.Id.Entry} to {rewardOwner.NetId}'s deck.");
            return false;
        }

        RemoveUnusedRewardCards(rewardCards, selected);
        CardCmd.PreviewCardPileAdd(addResult, 2f);
        Entry.Logger.Info(
            $"[DrawAndGuessMod] Timed gallery reward {selected.Id.Entry} added " +
            $"to {rewardOwner.NetId}'s deck from {_timedSuccessfulCards.Count} successful drawings.");
        return true;
    }

    private static void RemoveUnusedRewardCards(
        IEnumerable<CardModel> rewardCards,
        CardModel? selected)
    {
        foreach (CardModel card in rewardCards.Where(card =>
                     !ReferenceEquals(card, selected) &&
                     !card.HasBeenRemovedFromState))
        {
            card.RemoveFromState();
        }
    }

    private CardModel? RollTarget()
    {
        IReadOnlyList<CardModel> candidates = CardArtClassifier.GetChallengeCandidates(HostPlayer);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(ModText.Get("DRAW_AND_GUESS_MOD.VAKUUS_INFINITE_GALLERY.THERE_ARE_NO_CARDS_AVAILABLE_FOR_A"));
        }

        GalleryChallengeRoll roll = GalleryChallengeStore.ReserveRoll(HostPlayer);
        HashSet<string> usedTargetIds = new(roll.UsedTargetIds, StringComparer.Ordinal);
        List<CardModel> pool = candidates
            .Where(card => card is not Blank)
            .Where(card => !ErasedCardStore.IsErased(HostPlayer.RunState, card.Id))
            .Where(card => !usedTargetIds.Contains(card.Id.Entry))
            .OrderBy(card => card.Id.Entry, StringComparer.Ordinal)
            .ToList();
        if (pool.Count == 0)
        {
            return null;
        }

        CardModel target = pool[Rng.NextInt(pool.Count)];
        GalleryChallengeStore.RememberTarget(HostPlayer, target.Id.Entry);
        return target;
    }

    private static string GetChallengeDisplayName(CardModel card)
    {
        bool isNamedBasicCard = card.Rarity == CardRarity.Basic &&
                                card.Tags.Any(tag => tag is CardTag.Strike or CardTag.Defend);
        if (!isNamedBasicCard)
        {
            return card.Title;
        }

        CharacterModel? character = ModelDb.AllCharacters.FirstOrDefault(candidate =>
            candidate.CardPool.AllCardIds.Contains(card.Id) ||
            candidate.StartingDeck.Any(startingCard => startingCard.Id == card.Id));
        return character == null
            ? card.Title
            : $"{character.Title.GetFormattedText()} · {card.Title}";
    }

    private uint CreateSessionId()
    {
        ulong ownerId = HostPlayer.NetId;
        uint ownerHash = (uint)(ownerId ^ ownerId >> 32);
        uint eventHash = unchecked(DrawingNetSync.GalleryEventEpoch * 0x9E3779B9u);
        return 0xA7000000u ^ ownerHash ^ eventHash ^ (uint)_challengeNumber;
    }

    private static uint CreatePrivateDrawingSessionId(uint sessionId, ulong ownerId)
    {
        return sessionId ^ (uint)(ownerId ^ ownerId >> 32) ^ 0x51A7E123u;
    }

    private string OptionKey(string page, string option)
    {
        return $"{Id.Entry}.pages.{page}.options.{option}";
    }

    private void SetStringVar(string key, string value)
    {
        ((StringVar)DynamicVars[key]).StringValue = value;
    }

    private enum ChallengeMode
    {
        Timed,
        Standard
    }
}
