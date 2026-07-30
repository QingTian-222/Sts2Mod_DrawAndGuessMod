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
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace DrawAndGuessMod.Scripts.Events;

[RegisterSharedEvent]
public sealed class RelicAppraisalFair : ModEventTemplate
{
    private const int EntryCost = 100;
    private const int CostIncreasePerRound = 50;
    private const int MaxRounds = 3;
    private uint _appraisalId;
    private int _roundNumber = 1;

    public override bool IsShared => true;
    public override EventAssetProfile AssetProfile =>
        new(null!, DrawAndGuessAssets.RelicAppraisalFairPortraitPath, null!, null!);
    private Player EventOwner =>
        Owner ?? throw new InvalidOperationException("The relic appraisal fair has no owner.");
    private Player HostPlayer => EventOwner.RunState.Players.First(
        player => player.NetId == DrawingNetSync.HostNetId);

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new GoldVar("Cost", EntryCost),
        new StringVar("Awarded")
    ];

    protected override Task BeforeEventStarted(bool isPreFinished)
    {
        _roundNumber = 1;
        _appraisalId = CreateAppraisalId(_roundNumber);
        UpdateCostVars(EntryCost);

        if (!isPreFinished &&
            DrawingNetSync.IsLocalHost &&
            EventOwner.NetId == HostPlayer.NetId)
        {
            DrawingNetSync.BeginRelicAppraisalFair();
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
            new EventOption(this, everyoneCanPay ? EnterFair : null, key),
            new EventOption(
                this,
                LeaveFair,
                OptionKey("INITIAL", "LEAVE"))
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

        return GetAvailableAppraisalRelics(runState).Count >=
               runState.Players.Count;
    }

    private Task EnterFair()
    {
        return RunAppraisalRound(EntryCost);
    }

    private async Task RunAppraisalRound(int cost)
    {
        if (cost > 0)
        {
            await PlayerCmd.LoseGold(cost, EventOwner, GoldLossType.Spent);
        }
        string targetRelicId = await DrawingNetSync.WaitForAppraisalTargetAsync(
            _appraisalId,
            EventOwner.NetId);
        RelicModel target = FindRelic(targetRelicId);
        HashSet<ModelId> candidateIds = GetAvailableAppraisalRelics(
                EventOwner.RunState)
            .Select(relic => relic.Id)
            .ToHashSet();

        RelicAppraisalFairSubmission submission;
        if (LocalContext.IsMe(EventOwner))
        {
            DrawingScreenOptions options = new(
                ModText.Get(
                    $"鉴定题：{target.Title.GetFormattedText()}",
                    $"Appraisal Prompt: {target.Title.GetFormattedText()}"),
                ModText.Get(
                    "让瓦库认出题签上的遗物，便可为作品署名并提交鉴定。其他人只会看到作品名。画布是透明的，右键会抹去颜色。",
                    "Once VAKUU recognizes the relic on your prompt, you may title the work and submit it for appraisal. Other players will see only its title. The canvas is transparent; RMB clears color."),
                PeekTooltip: ModText.Get("查看事件", "View event"),
                InitialCanvasMode: DrawingCanvasMode.Relic,
                AllowCanvasModeSwitch: false);
            RelicDrawingResult? drawing = await DrawingScreen.ShowRelicAsync(
                EventOwner,
                _appraisalId ^ (uint)EventOwner.NetId,
                target,
                options,
                candidateIds);
            if (drawing == null)
            {
                throw new InvalidOperationException("The required relic drawing closed before submission.");
            }

            submission = new RelicAppraisalFairSubmission(
                EventOwner.NetId,
                target.Id.Entry,
                drawing.WorkTitle,
                drawing.PngBytes);
            DrawingNetSync.PublishAppraisalSubmission(_appraisalId, submission);
        }
        else
        {
            submission = await DrawingNetSync.WaitForAppraisalSubmissionAsync(
                _appraisalId,
                EventOwner.NetId);
        }

        IReadOnlyList<RelicAppraisalFairSubmission> allSubmissions;
        try
        {
            allSubmissions = await WaitForAllSubmissions();
        }
        finally
        {
            DrawingScreen.CloseCompletedRelicScreen();
        }
        HashSet<ModelId> validationCandidateIds = GetAvailableAppraisalRelics(
                EventOwner.RunState)
            .Select(relic => relic.Id)
            .ToHashSet();
        foreach (RelicAppraisalFairSubmission candidate in allSubmissions)
        {
            ValidateSubmission(candidate, validationCandidateIds);
        }
        IReadOnlyDictionary<ulong, string> results =
            await RelicAppraisalFairTreasureFlow.RunAsync(
                _appraisalId,
                (MegaCrit.Sts2.Core.Runs.RunState)EventOwner.RunState,
                allSubmissions);
        if (results.TryGetValue(EventOwner.NetId, out string? awardedRelicId))
        {
            RelicModel awarded = FindRelic(awardedRelicId);
            ((StringVar)DynamicVars["Awarded"]).StringValue =
                RelicAppraisalFairArtworkStore.TryGetAwarded(
                    awarded,
                    out RelicAppraisalFairPresentation? presentation)
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
        List<RelicModel> available = GetAvailableAppraisalRelics(
                EventOwner.RunState)
            .OrderBy(relic => relic.Id.Entry, StringComparer.Ordinal)
            .ToList();
        Shuffle(available);

        List<Player> players = EventOwner.RunState.Players
            .OrderBy(player => player.NetId)
            .ToList();
        for (int index = 0; index < players.Count; index++)
        {
            DrawingNetSync.PublishAppraisalTarget(
                _appraisalId,
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

        uint previousAppraisalId = _appraisalId;
        _appraisalId = CreateAppraisalId(_roundNumber + 1);
        DrawingNetSync.BeginRelicAppraisalFair();
        PublishUniqueTargets();
        _appraisalId = previousAppraisalId;
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
                    ContinueFair,
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
            LeaveFair,
            OptionKey("DONE", "LEAVE")));
        return options;
    }

    private async Task ContinueFair()
    {
        if (_roundNumber >= MaxRounds)
        {
            await LeaveFair();
            return;
        }

        int nextRound = _roundNumber + 1;
        int cost = GetRoundCost(nextRound);
        _roundNumber = nextRound;
        _appraisalId = CreateAppraisalId(_roundNumber);
        UpdateCostVars(cost);
        await RunAppraisalRound(cost);
    }

    private Task LeaveFair()
    {
        SetEventFinished(PageDescription("LEAVE"));
        return Task.CompletedTask;
    }

    private int CountAvailableTargets()
    {
        return GetAvailableAppraisalRelics(EventOwner.RunState).Count;
    }

    private bool CanAffordRound(int cost)
    {
        return EventOwner.RunState.Players.All(
            player => player.Gold >= cost);
    }

    private void UpdateCostVars(int cost)
    {
        DynamicVars["Cost"].BaseValue = cost;
    }

    private static int GetRoundCost(int roundNumber)
    {
        return EntryCost +
               Math.Max(0, roundNumber - 1) * CostIncreasePerRound;
    }

    private async Task<IReadOnlyList<RelicAppraisalFairSubmission>> WaitForAllSubmissions()
    {
        List<Player> players = EventOwner.RunState.Players
            .OrderBy(player => player.NetId)
            .ToList();
        RelicAppraisalFairSubmission[] submissions = await Task.WhenAll(players.Select(
            player => DrawingNetSync.WaitForAppraisalSubmissionAsync(
                _appraisalId,
                player.NetId)));
        return submissions;
    }

    private void ValidateSubmission(
        RelicAppraisalFairSubmission submission,
        IReadOnlySet<ModelId> candidateIds)
    {
        string expectedTarget = DrawingNetSync.WaitForAppraisalTargetAsync(
            _appraisalId,
            submission.OwnerId).GetAwaiter().GetResult();
        if (!string.Equals(
                expectedTarget,
                submission.TargetRelicId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Relic appraisal submission target mismatch for {submission.OwnerId}.");
        }

        Image image = new();
        if (image.LoadPngFromBuffer(submission.PngBytes) != Error.Ok)
        {
            throw new InvalidOperationException(
                $"Relic appraisal submission from {submission.OwnerId} has an invalid PNG.");
        }
        RelicArtGuess? guess = RelicArtClassifier.GuessTopOne(
            image,
            candidateIds);
        if (guess?.Relic.Id.Entry != expectedTarget)
        {
            throw new InvalidOperationException(
                $"Host rejected relic appraisal submission from {submission.OwnerId}: " +
                $"expected {expectedTarget}, guessed {guess?.Relic.Id.Entry ?? "none"}.");
        }
    }

    private RelicModel FindRelic(string relicId)
    {
        return ModelDb.AllRelics.FirstOrDefault(relic =>
                   string.Equals(relic.Id.Entry, relicId, StringComparison.Ordinal))
               ?? throw new InvalidOperationException(
                   $"The relic appraisal fair references missing relic '{relicId}'.");
    }

    private static IReadOnlyList<RelicModel> GetAvailableAppraisalRelics(
        IRunState runState)
    {
        HashSet<ModelId> ownedIds = runState.Players
            .SelectMany(player => player.Relics)
            .Select(relic => relic.Id)
            .ToHashSet();

        return RelicArtClassifier.GetEligibleRelics()
            .Where(relic => !ownedIds.Contains(relic.Id))
            .OrderBy(relic => relic.Id.Entry, StringComparer.Ordinal)
            .ToList();
    }

    private uint CreateAppraisalId(int roundNumber)
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
