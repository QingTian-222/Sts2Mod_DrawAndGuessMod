using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Ai;
using DrawAndGuessMod.Scripts.Assets;
using DrawAndGuessMod.Scripts.Cards;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.Networking;
using DrawAndGuessMod.Scripts.State;
using DrawAndGuessMod.Scripts.Ui;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Random;
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
        if (EventOwner.NetId == HostPlayer.NetId)
        {
            DrawingNetSync.BeginGalleryEvent();
        }
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
            await ClaimTimedGalleryRewardIfEligible();
            SetEventFinished(PageDescription("EXHAUSTED"));
            return;
        }

        string targetDisplayName = GetChallengeDisplayName(target);
        SetStringVar("Target", targetDisplayName);
        SetStringVar("Chosen", ModText.Get("无", "None"));
        DynamicVars["Reward"].BaseValue = 0;

        _challengeNumber++;
        Entry.Logger.Info(
            $"[DrawAndGuessMod] Gallery challenge {_challengeNumber} started for {EventOwner.NetId}: " +
            $"mode={_mode}, target={target.Id.Entry}, session={sessionId}.");
        DrawingScreenOptions screenOptions = new(
            ModText.Get(
                $"目标：{targetDisplayName}",
                $"Target: {targetDisplayName}"),
            _mode == ChallengeMode.Timed
                ? ModText.Get(
                    "在倒计时结束前完成画作。确认后，从瓦库猜出的三张牌中选出目标牌。",
                    "Finish before time runs out, then choose the target from VAKUU's three guesses.")
                : ModText.Get(
                    "完成画作后，从瓦库猜出的三张牌中选出目标牌。",
                    "Finish the drawing, then choose the target from VAKUU's three guesses."),
            _mode == ChallengeMode.Timed ? TimedChallengeSeconds : null,
            ModText.Get("查看事件", "View event"));

        DrawingResult? drawing = await DrawingScreen.ShowAsync(HostPlayer, sessionId, screenOptions);
        if (drawing == null)
        {
            ResolveFailure();
            return;
        }

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
        ArtworkStore.Set(EventOwner.RunState, selected.Id.Entry, drawing.PngBytes);
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
            await PlayerCmd.GainGold(reward, EventOwner);
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
            1 => ModText.Get(
                "[jitter]瓦库嘴角微微上扬，或许多画一些能有意想不到的奖励。[/jitter]",
                "[jitter]The corner of VAKUU's mouth lifts slightly. Perhaps drawing a few more will bring an unexpected reward.[/jitter]"),
            2 => ModText.Get(
                "[jitter]瓦库有些期待了。[/jitter]",
                "[jitter]VAKUU is beginning to look expectant.[/jitter]"),
            _ => ModText.Get(
                "[jitter]瓦库很满意。[/jitter]",
                "[jitter]VAKUU is very pleased.[/jitter]")
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
        bool tookCard = await ClaimTimedGalleryRewardIfEligible();
        SetEventFinished(PageDescription(tookCard ? "TAKE_REWARD" : "LEAVE"));
    }

    private bool CanClaimTimedGalleryReward =>
        _mode == ChallengeMode.Timed &&
        _timedSuccessCount >= TimedGalleryRewardWins &&
        _timedSuccessfulCards.Count >= TimedGalleryRewardWins &&
        !_timedGalleryRewardClaimed;

    private async Task<bool> ClaimTimedGalleryRewardIfEligible()
    {
        if (!CanClaimTimedGalleryReward)
        {
            return false;
        }

        _timedGalleryRewardClaimed = true;
        List<CardModel> choices = _timedSuccessfulCards
            .Select(card => EventOwner.RunState.CreateCard(card, EventOwner))
            .ToList();
        CardSelectorPrefs prefs = new(
            L10NLookup($"{Id.Entry}.selectionScreenPrompt"),
            1);
        CardModel? selected = (await CardSelectCmd.FromSimpleGrid(
                new BlockingPlayerChoiceContext(),
                choices,
                EventOwner,
                prefs))
            .FirstOrDefault();
        if (selected == null)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Gallery reward selection returned no card for {EventOwner.NetId}.");
            return false;
        }

        CardPileAddResult result = await CardPileCmd.Add(selected, PileType.Deck);
        if (!result.success)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Failed to add gallery reward {selected.Id.Entry} to {EventOwner.NetId}'s deck.");
            return false;
        }

        CardCmd.PreviewCardPileAdd(result, 2f, CardPreviewStyle.EventLayout);
        Entry.Logger.Info(
            $"[DrawAndGuessMod] Gallery reward {selected.Id.Entry} claimed by {EventOwner.NetId} " +
            $"from {_timedSuccessfulCards.Count} successful cards.");
        return true;
    }

    private CardModel? RollTarget()
    {
        IReadOnlyList<CardModel> candidates = CardArtClassifier.GetChallengeCandidates(HostPlayer);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(ModText.Get(
                "当前识别范围中没有可用于挑战的卡牌。",
                "There are no cards available for a gallery challenge under the current recognition settings."));
        }

        GalleryChallengeRoll roll = GalleryChallengeStore.ReserveRoll(HostPlayer);
        HashSet<string> usedTargetIds = new(roll.UsedTargetIds, StringComparer.Ordinal);
        List<CardModel> pool = candidates
            .Where(card => card is not Blank)
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
