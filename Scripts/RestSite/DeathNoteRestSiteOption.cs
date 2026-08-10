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
            ModText.Get("DRAW_AND_GUESS_MOD.DEATH_NOTE_REST_SITE_OPTION.DEATH_SKETCHBOOK"),
            ModText.Get("DRAW_AND_GUESS_MOD.DEATH_NOTE_REST_SITE_OPTION.DRAW_A_CARD_ILLUSTRATION_THEN_CHOOSE_ONE"),
            DrawAndGuessSettings.DrawingTimeLimitSeconds,
            ModText.Get("DRAW_AND_GUESS_MOD.DEATH_NOTE_REST_SITE_OPTION.VIEW_REST_SITE"),
            CloseWhenRestSiteEnds: true,
            CandidateScope: GuessCandidateScope.PartyCharactersAndShared);

        DrawingResult? drawing = await DrawingScreen.ShowAsync(Owner, sessionId, screenOptions);
        if (drawing == null)
        {
            return false;
        }

        string? memorialArtworkId = await MemorialSketchbookStore.CaptureCardDrawingAsync(
            runState,
            Owner.NetId,
            sessionId,
            drawing);

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
        MemorialSketchbookStore.AssignCard(
            runState,
            memorialArtworkId,
            selected,
            drawing.PngBytes);
        ArtworkStore.Set(runState, selected, drawing.PngBytes);
        bool newlyErased = ErasedCardStore.Erase(runState, selectedId);
        RemoveChoiceModels(choices);
        if (!newlyErased)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Death Sketchbook selected already-erased card {selectedId.Entry}.");
            return false;
        }

        Owner.Relics.OfType<DeathNote>().FirstOrDefault()?.Flash();
        await ErasedCardStore.RemoveExistingCardsAsync(runState, selectedId);
        Entry.Logger.Info(
            $"[DrawAndGuessMod] Death Sketchbook erased {selectedId.Entry} from run at floor " +
            $"{runState.TotalFloor}; its Memorial Sketchbook artwork was retained.");
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
