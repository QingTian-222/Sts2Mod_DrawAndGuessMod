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
    private static readonly int EntryCost = 0;
    private uint _auctionId;

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
        _auctionId = CreateAuctionId();
        int minimumGold = EventOwner.RunState.Players.Min(player => player.Gold);
        DynamicVars["Remaining"].BaseValue = Math.Max(0, EntryCost - minimumGold);

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
        if (runState.CurrentActIndex == 0)
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

    private async Task EnterAuction()
    {
        if (EntryCost > 0)
        {
            await PlayerCmd.LoseGold(EntryCost, EventOwner, GoldLossType.Spent);
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
                    $"题目：{target.Title.GetFormattedText()}",
                    $"Target: {target.Title.GetFormattedText()}"),
                ModText.Get(
                    "每完成一笔都会显示当前识别结果。只有识别结果与题目完全一致时才能提交。遗物图片背景为透明；右键默认绘制透明色。",
                    "The current guess updates after every completed action. You may submit only when it exactly matches the target. Relic artwork uses a transparent background; the right mouse button draws transparency by default."),
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

        SetEventFinished(PageDescription("DONE"));
    }

    private void PublishUniqueTargets()
    {
        HashSet<ModelId> owned = EventOwner.RunState.Players
            .SelectMany(player => player.Relics)
            .Select(relic => relic.Id)
            .ToHashSet();
        List<RelicModel> available = RelicArtClassifier.GetEligibleRelics()
            .Where(relic => !owned.Contains(relic.Id))
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

    private uint CreateAuctionId()
    {
        ulong hostId = DrawingNetSync.HostNetId;
        uint hostHash = (uint)(hostId ^ hostId >> 32);
        return 0xA8000000u ^
               hostHash ^
               unchecked((uint)EventOwner.RunState.TotalFloor * 0x9E3779B9u);
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
