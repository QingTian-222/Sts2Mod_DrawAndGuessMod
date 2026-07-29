using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Ai;
using DrawAndGuessMod.Scripts.Assets;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.Networking;
using DrawAndGuessMod.Scripts.State;
using DrawAndGuessMod.Scripts.Ui;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Saves;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace DrawAndGuessMod.Scripts.Events;

[RegisterSharedEvent]
public sealed class RelicAuction : ModEventTemplate
{
    private const int EntryCost = 100;
    private const int CostIncreasePerRound = 50;
    private const int MaxRounds = 3;
    private uint _auctionId;
    private int _roundNumber = 1;
    private HashSet<ModelId> _usedTargetIds = new();

    public override bool IsShared => true;
    public override EventAssetProfile AssetProfile =>
        new(null!, DrawAndGuessAssets.GalleryPortraitPath, null!, null!);
    private Player EventOwner =>
        Owner ?? throw new InvalidOperationException("The relic auction has no owner.");
    private Player HostPlayer => EventOwner.RunState.Players.First(
        player => player.NetId == DrawingNetSync.HostNetId);

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new GoldVar("Cost", EntryCost),
        new GoldVar("Remaining", 0),
        new StringVar("Awarded")
    ];

    protected override Task BeforeEventStarted(bool isPreFinished)
    {
        _roundNumber = 1;
        _usedTargetIds = new HashSet<ModelId>();
        _auctionId = CreateAuctionId(_roundNumber);
        UpdateCostVars(EntryCost);

        if (!isPreFinished &&
            DrawingNetSync.IsLocalHost &&
            EventOwner.NetId == HostPlayer.NetId)
        {
            DrawingNetSync.BeginRelicAuction();
            PublishUniqueTargets();
        }
        return Task.CompletedTask;
    }

    protected override IReadOnlyList<EventOption> GenerateInitialOptions()
    {
        bool everyoneCanPay = EventOwner.RunState.Players.All(player => player.Gold >= EntryCost);
        string key = everyoneCanPay
            ? OptionKey("INITIAL", "ENTER")
            : OptionKey("INITIAL", "INSUFFICIENT");
        return
        [
            new EventOption(this, everyoneCanPay ? EnterAuction : null, key)
        ];
    }

    public override bool IsAllowed(MegaCrit.Sts2.Core.Runs.IRunState runState)
    {
        if (runState.Players.Count <= 1 ||
            runState.CurrentActIndex == 0)
        {
            return false;
        }

        if (!runState.Players.All(player => player.Gold >= EntryCost))
        {
            return false;
        }

        if (!runState.Players.All(player => player.Relics.Count(relic => relic.IsTradable) >= 5))
        {
            return false;
        }

        HashSet<ModelId> owned = runState.Players
            .SelectMany(player => player.Relics)
            .Select(relic => relic.Id)
            .ToHashSet();
        return RelicArtClassifier.GetEligibleRelics().Count(relic => !owned.Contains(relic.Id)) >=
               runState.Players.Count;
    }

    private Task EnterAuction()
    {
        return RunAuctionRound(EntryCost);
    }

    private async Task RunAuctionRound(int cost)
    {
        if (cost > 0)
        {
            await PlayerCmd.LoseGold(cost, EventOwner, GoldLossType.Spent);
        }
        string targetRelicId = await DrawingNetSync.WaitForAuctionTargetAsync(
            _auctionId,
            EventOwner.NetId);
        RelicModel target = FindRelic(targetRelicId);

        RelicAuctionSubmission submission;
        if (LocalContext.IsMe(EventOwner))
        {
            DrawingScreenOptions options = new(
                ModText.Get(
                    $"题签：{target.Title.GetFormattedText()}",
                    $"Commission: {target.Title.GetFormattedText()}"),
                ModText.Get(
                    "让瓦库认出题签上的遗物，便可署名送拍。画布是透明的，右键会抹去颜色。",
                    "Once VAKUU recognizes the relic on your slip, you may sign it and send it to auction. The canvas is transparent; RMB clears color."),
                PeekTooltip: ModText.Get("查看事件", "View event"),
                InitialCanvasMode: DrawingCanvasMode.Relic,
                AllowCanvasModeSwitch: false);
            RelicDrawingResult? drawing = await DrawingScreen.ShowRelicAsync(
                EventOwner,
                _auctionId ^ (uint)EventOwner.NetId,
                target,
                options);
            if (drawing == null)
            {
                throw new InvalidOperationException("The required relic drawing closed before submission.");
            }

            submission = new RelicAuctionSubmission(
                EventOwner.NetId,
                target.Id.Entry,
                drawing.WorkTitle,
                drawing.PngBytes);
            DrawingNetSync.PublishAuctionSubmission(_auctionId, submission);
        }
        else
        {
            submission = await DrawingNetSync.WaitForAuctionSubmissionAsync(
                _auctionId,
                EventOwner.NetId);
        }

        IReadOnlyList<RelicAuctionSubmission> allSubmissions =
            await WaitForAllSubmissions();
        foreach (RelicAuctionSubmission candidate in allSubmissions)
        {
            ValidateSubmission(candidate);
            _usedTargetIds.Add(
                new ModelId("RELIC", candidate.TargetRelicId));
        }

        IReadOnlyDictionary<ulong, string> results =
            await RelicAuctionTreasureFlow.RunAsync(
                _auctionId,
                (MegaCrit.Sts2.Core.Runs.RunState)EventOwner.RunState,
                allSubmissions);
        if (results.TryGetValue(EventOwner.NetId, out string? awardedRelicId))
        {
            RelicModel awarded = FindRelic(awardedRelicId);
            ((StringVar)DynamicVars["Awarded"]).StringValue =
                RelicAuctionArtworkStore.TryGetAwarded(
                    awarded,
                    out RelicAuctionPresentation? presentation)
                    ? presentation.WorkTitle
                    : awarded.Title.GetFormattedText();
        }
        else
        {
            ((StringVar)DynamicVars["Awarded"]).StringValue =
                ModText.Get("无", "None");
        }

        PrepareNextRoundTargets();
        SetEventState(
            PageDescription("DONE"),
            GenerateRoundResultOptions());
    }

    private void PublishUniqueTargets()
    {
        HashSet<ModelId> owned = EventOwner.RunState.Players
            .SelectMany(player => player.Relics)
            .Select(relic => relic.Id)
            .ToHashSet();
        List<RelicModel> available = RelicArtClassifier.GetEligibleRelics()
            .Where(relic =>
                !owned.Contains(relic.Id) &&
                !_usedTargetIds.Contains(relic.Id))
            .OrderBy(relic => relic.Id.Entry, StringComparer.Ordinal)
            .ToList();
        Shuffle(available);

        List<Player> players = EventOwner.RunState.Players
            .OrderBy(player => player.NetId)
            .ToList();
        for (int index = 0; index < players.Count; index++)
        {
            DrawingNetSync.PublishAuctionTarget(
                _auctionId,
                players[index].NetId,
                available[index].Id.Entry);
        }
    }

    private void PrepareNextRoundTargets()
    {
        if (_roundNumber >= MaxRounds)
        {
            return;
        }

        int nextCost = GetRoundCost(_roundNumber + 1);
        UpdateCostVars(nextCost);
        if (!CanAffordRound(nextCost) ||
            CountAvailableTargets() < EventOwner.RunState.Players.Count ||
            !DrawingNetSync.IsLocalHost ||
            EventOwner.NetId != HostPlayer.NetId)
        {
            return;
        }

        uint previousAuctionId = _auctionId;
        _auctionId = CreateAuctionId(_roundNumber + 1);
        DrawingNetSync.BeginRelicAuction();
        PublishUniqueTargets();
        _auctionId = previousAuctionId;
    }

    private IReadOnlyList<EventOption> GenerateRoundResultOptions()
    {
        List<EventOption> options = new(2);
        if (_roundNumber < MaxRounds)
        {
            int nextCost = GetRoundCost(_roundNumber + 1);
            UpdateCostVars(nextCost);
            if (CountAvailableTargets() < EventOwner.RunState.Players.Count)
            {
                options.Add(new EventOption(
                    this,
                    null,
                    OptionKey("DONE", "SOLD_OUT")));
            }
            else if (CanAffordRound(nextCost))
            {
                options.Add(new EventOption(
                    this,
                    ContinueAuction,
                    OptionKey("DONE", "CONTINUE")));
            }
            else
            {
                options.Add(new EventOption(
                    this,
                    null,
                    OptionKey("DONE", "INSUFFICIENT")));
            }
        }

        options.Add(new EventOption(
            this,
            LeaveAuction,
            OptionKey("DONE", "LEAVE")));
        return options;
    }

    private async Task ContinueAuction()
    {
        if (_roundNumber >= MaxRounds)
        {
            await LeaveAuction();
            return;
        }

        int nextRound = _roundNumber + 1;
        int cost = GetRoundCost(nextRound);
        _roundNumber = nextRound;
        _auctionId = CreateAuctionId(_roundNumber);
        UpdateCostVars(cost);
        await RunAuctionRound(cost);
    }

    private Task LeaveAuction()
    {
        SetEventFinished(PageDescription("LEAVE"));
        return Task.CompletedTask;
    }

    private int CountAvailableTargets()
    {
        HashSet<ModelId> owned = EventOwner.RunState.Players
            .SelectMany(player => player.Relics)
            .Select(relic => relic.Id)
            .ToHashSet();
        return RelicArtClassifier.GetEligibleRelics().Count(relic =>
            !owned.Contains(relic.Id) &&
            !_usedTargetIds.Contains(relic.Id));
    }

    private bool CanAffordRound(int cost)
    {
        return EventOwner.RunState.Players.All(
            player => player.Gold >= cost);
    }

    private void UpdateCostVars(int cost)
    {
        DynamicVars["Cost"].BaseValue = cost;
        int minimumGold = EventOwner.RunState.Players.Min(
            player => player.Gold);
        DynamicVars["Remaining"].BaseValue =
            Math.Max(0, cost - minimumGold);
    }

    private static int GetRoundCost(int roundNumber)
    {
        return EntryCost +
               Math.Max(0, roundNumber - 1) * CostIncreasePerRound;
    }

    private async Task<IReadOnlyList<RelicAuctionSubmission>> WaitForAllSubmissions()
    {
        List<Player> players = EventOwner.RunState.Players
            .OrderBy(player => player.NetId)
            .ToList();
        RelicAuctionSubmission[] submissions = await Task.WhenAll(players.Select(
            player => DrawingNetSync.WaitForAuctionSubmissionAsync(
                _auctionId,
                player.NetId)));
        return submissions;
    }

    private void ValidateSubmission(RelicAuctionSubmission submission)
    {
        string expectedTarget = DrawingNetSync.WaitForAuctionTargetAsync(
            _auctionId,
            submission.OwnerId).GetAwaiter().GetResult();
        if (!string.Equals(
                expectedTarget,
                submission.TargetRelicId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Relic auction submission target mismatch for {submission.OwnerId}.");
        }

        Image image = new();
        if (image.LoadPngFromBuffer(submission.PngBytes) != Error.Ok)
        {
            throw new InvalidOperationException(
                $"Relic auction submission from {submission.OwnerId} has an invalid PNG.");
        }
        RelicArtGuess? guess = RelicArtClassifier.GuessTopOne(image);
        if (guess?.Relic.Id.Entry != expectedTarget)
        {
            throw new InvalidOperationException(
                $"Host rejected relic auction submission from {submission.OwnerId}: " +
                $"expected {expectedTarget}, guessed {guess?.Relic.Id.Entry ?? "none"}.");
        }
    }

    private RelicModel FindRelic(string relicId)
    {
        return ModelDb.AllRelics.FirstOrDefault(relic =>
                   string.Equals(relic.Id.Entry, relicId, StringComparison.Ordinal))
               ?? throw new InvalidOperationException(
                   $"The relic auction references missing relic '{relicId}'.");
    }

    private uint CreateAuctionId(int roundNumber)
    {
        ulong hostId = DrawingNetSync.HostNetId;
        uint hostHash = (uint)(hostId ^ hostId >> 32);
        return 0xA8000000u ^
               hostHash ^
               unchecked((uint)EventOwner.RunState.TotalFloor * 0x9E3779B9u) ^
               unchecked((uint)roundNumber * 0x85EBCA6Bu);
    }

    private void Shuffle<T>(IList<T> items)
    {
        for (int index = items.Count - 1; index > 0; index--)
        {
            int other = Rng.NextInt(index + 1);
            (items[index], items[other]) = (items[other], items[index]);
        }
    }

    private string OptionKey(string page, string option)
    {
        return $"{Id.Entry}.pages.{page}.options.{option}";
    }
}
