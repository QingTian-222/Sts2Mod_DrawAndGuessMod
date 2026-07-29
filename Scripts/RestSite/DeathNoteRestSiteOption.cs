using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Ai;
using DrawAndGuessMod.Scripts.Config;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.Relics;
using DrawAndGuessMod.Scripts.State;
using DrawAndGuessMod.Scripts.Ui;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Scaffolding.Content;

namespace DrawAndGuessMod.Scripts.RestSite;

public sealed class DeathNoteRestSiteOption : ModRestSiteOptionTemplate
{
    public const string StableId = "DRAW_AND_GUESS_DEATH_NOTE";
    public const string TitleKey = "OPTION_DRAW_AND_GUESS_DEATH_NOTE.name";
    public const string DescriptionKey = "OPTION_DRAW_AND_GUESS_DEATH_NOTE.description";
    private const string IconPath = "res://images/ui/rest_site/option_draw.png";
    private static readonly object AttemptLock = new();
    private static readonly Dictionary<(ulong OwnerId, int Floor), uint> Attempts = new();

    public override string OptionId => StableId;
    public override RestSiteOptionAssetProfile AssetProfile => new(IconPath);
    public override LocString? CustomTitle => new("rest_site_ui", TitleKey);
    public override LocString Description => new("rest_site_ui", DescriptionKey);

    public DeathNoteRestSiteOption(Player owner)
        : base(owner)
    {
    }

    public static void ResetSessions()
    {
        lock (AttemptLock)
        {
            Attempts.Clear();
        }
    }

    public override async Task<bool> OnSelect()
    {
        if (Owner.RunState is not RunState runState)
        {
            return false;
        }

        uint sessionId = CreateSessionId(runState);
        DrawingScreenOptions screenOptions = new(
            ModText.Get("\u6b7b\u4ea1\u7ed8\u672c", "Death Sketchbook"),
            ModText.Get(
                "\u7ed8\u5236\u4e00\u5f20\u5361\u9762\uff0c\u7136\u540e\u4ece\u74e6\u5e93\u731c\u6d4b\u7684\u4e09\u5f20\u724c\u4e2d\u9009\u62e9\u4e00\u5f20\u3002\u5b83\u5c06\u4ece\u672c\u5c40\u6e38\u620f\u4e2d\u5f7b\u5e95\u6d88\u5931\u3002",
                "Draw a card illustration, then choose one of VAKUU's three guesses. It will disappear completely from this run."),
            DrawAndGuessSettings.DrawingTimeLimitSeconds,
            ModText.Get("\u67e5\u770b\u706b\u5806", "View rest site"),
            CloseWhenRestSiteEnds: true,
            CandidateScope: GuessCandidateScope.PartyCharactersAndShared);

        DrawingResult? drawing = await DrawingScreen.ShowAsync(Owner, sessionId, screenOptions);
        if (drawing == null)
        {
            return false;
        }

        List<CardModel> choices = drawing.Guess.NearestCards
            .Take(3)
            .Select(candidate => runState.CreateCard(candidate, Owner))
            .ToList();
        if (choices.Count == 0)
        {
            return false;
        }

        CardModel? selected = await CardSelectCmd.FromChooseACardScreen(
            new BlockingPlayerChoiceContext(),
            choices,
            Owner);
        if (selected == null)
        {
            RemoveChoiceModels(choices);
            return false;
        }

        ModelId selectedId = selected.Id;
        ArtworkStore.Set(runState, selected, drawing.PngBytes);
        bool newlyErased = ErasedCardStore.Erase(runState, selectedId);
        RemoveChoiceModels(choices);
        if (!newlyErased)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Death Sketchbook selected already-erased card {selectedId.Entry}.");
            return false;
        }

        int removedMemorialEntries =
            GalleryChallengeStore.RemoveMemorialCard(runState, selectedId);
        Owner.Relics.OfType<DeathNote>().FirstOrDefault()?.Flash();
        await ErasedCardStore.RemoveExistingCardsAsync(runState, selectedId);
        Entry.Logger.Info(
            $"[DrawAndGuessMod] Death Sketchbook erased {selectedId.Entry} from run at floor " +
            $"{runState.TotalFloor} and removed {removedMemorialEntries} memorial entry/entries.");
        return true;
    }

    private static void RemoveChoiceModels(IEnumerable<CardModel> choices)
    {
        foreach (CardModel choice in choices.Where(card => !card.HasBeenRemovedFromState))
        {
            choice.RemoveFromState();
        }
    }

    private uint CreateSessionId(RunState runState)
    {
        uint attempt;
        lock (AttemptLock)
        {
            (ulong OwnerId, int Floor) key = (Owner.NetId, runState.TotalFloor);
            Attempts.TryGetValue(key, out attempt);
            Attempts[key] = attempt + 1u;
        }

        uint ownerHash = (uint)(Owner.NetId ^ Owner.NetId >> 32);
        uint floorHash = unchecked((uint)runState.TotalFloor * 0x9E3779B9u);
        return 0xD3400000u ^ ownerHash ^ floorHash ^ attempt;
    }
}
