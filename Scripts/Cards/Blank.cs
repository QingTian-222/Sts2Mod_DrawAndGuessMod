using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Ai;
using DrawAndGuessMod.Scripts.State;
using DrawAndGuessMod.Scripts.Ui;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Interop.AutoRegistration;

namespace DrawAndGuessMod.Scripts.Cards;

[RegisterCard(typeof(ColorlessCardPool))]
public sealed class Blank : CardModel
{
    private BlankReplaySequence _replaySequence = new();
    private List<PendingBlankChoice> _pendingReplayChoices = new();

    public override CardMultiplayerConstraint MultiplayerConstraint => CardMultiplayerConstraint.None;
    public override bool CanBeGeneratedInCombat => DrawingRunRules.IsGameplayEnabledForCurrentRun();
    public override bool CanBeGeneratedByModifiers => DrawingRunRules.IsGameplayEnabledForCurrentRun();
    public override string PortraitPath => MissingPortraitPath;
    public override CardPoolModel VisualCardPool => ModelDb.CardPool<ColorlessCardPool>();
    public override IEnumerable<CardKeyword> CanonicalKeywords => [CardKeyword.Exhaust];

    public Blank() : base(1, CardType.Skill, CardRarity.Rare, TargetType.AnyPlayer)
    {
    }

    protected override void AfterCloned()
    {
        base.AfterCloned();
        _replaySequence = new BlankReplaySequence();
        _pendingReplayChoices = new List<PendingBlankChoice>();
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        if (cardPlay.PlayIndex == 0)
        {
            _pendingReplayChoices.Clear();
        }

        uint combatCardIndex = NetCombatCard.FromModel(this).CombatCardIndex;
        uint sessionId = _replaySequence.NextSessionId(combatCardIndex);
        Entry.Logger.Info(
            $"[DrawAndGuessMod] Starting Blank play {cardPlay.PlayIndex + 1}/{cardPlay.PlayCount}: " +
            $"owner={Owner.NetId}, target={cardPlay.Target?.Player?.NetId ?? Owner.NetId}, " +
            $"combatCard={combatCardIndex}, session={sessionId}.");

        PendingBlankChoice? pendingChoice = await PrepareChoice(cardPlay, sessionId);
        if (pendingChoice != null)
        {
            _pendingReplayChoices.Add(pendingChoice);
        }

        if (cardPlay.PlayIndex + 1 < cardPlay.PlayCount)
        {
            return;
        }

        try
        {
            foreach (PendingBlankChoice choice in _pendingReplayChoices)
            {
                await ResolveChoice(choiceContext, choice);
            }
        }
        finally
        {
            _pendingReplayChoices.Clear();
        }
    }

    private async Task<PendingBlankChoice?> PrepareChoice(CardPlay cardPlay, uint sessionId)
    {
        Player recipient = cardPlay.Target?.Player ?? Owner;
        DrawingResult? drawing = await DrawingScreen.ShowAsync(
            Owner,
            sessionId,
            defaultTimeLimitSeconds: DrawingRunRules.GetDrawingTimeLimitSeconds(Owner.RunState),
            isRegularBlank: true);
        if (drawing == null)
        {
            return null;
        }

        string? memorialArtworkId = Owner.RunState is RunState runState
            ? await MemorialSketchbookStore.CaptureCardDrawingAsync(
                runState,
                Owner.NetId,
                sessionId,
                drawing)
            : null;

        ICombatState? combatState = CombatState;
        if (combatState == null)
        {
            Entry.Logger.Warn("[DrawAndGuessMod] Combat ended before the guessed card could be created.");
            return null;
        }

        List<CardModel> options = drawing.Guess.NearestCards
            .Take(3)
            .Select(candidate => drawing.SkipAddingToDeck
                ? combatState.CreateCard(candidate, recipient)
                : Owner.RunState.CreateCard(candidate, recipient))
            .ToList();
        if (options.Count == 0)
        {
            Entry.Logger.Warn("[DrawAndGuessMod] The classifier returned no card choices.");
            return null;
        }

        if (IsUpgraded)
        {
            foreach (CardModel option in options.Where(option => option.IsUpgradable))
            {
                CardCmd.Upgrade(option);
            }
        }

        return new PendingBlankChoice(recipient, drawing, sessionId, memorialArtworkId, combatState, options);
    }

    private async Task ResolveChoice(PlayerChoiceContext choiceContext, PendingBlankChoice choice)
    {
        CardModel? selectedCard = await CardSelectCmd.FromChooseACardScreen(
            choiceContext,
            choice.Options,
            choice.Recipient);
        if (selectedCard == null)
        {
            return;
        }

        if (Owner.RunState is RunState selectedRunState)
        {
            MemorialSketchbookStore.AssignCard(
                selectedRunState,
                choice.MemorialArtworkId,
                selectedCard,
                choice.Drawing.PngBytes);
        }
        ArtworkStore.Set(Owner.RunState, selectedCard, choice.Drawing.PngBytes);
        DrawingHistoryStore.RecordCard(
            Owner.RunState,
            Owner.NetId,
            choice.SessionId,
            selectedCard,
            choice.Drawing.PngBytes);
        BlankSelectionStore.Remember(Owner.RunState, selectedCard.Id);

        if (choice.Drawing.SkipAddingToDeck)
        {
            CardPileAddResult handResult = await CardPileCmd.AddGeneratedCardToCombat(
                selectedCard,
                PileType.Hand,
                Owner);
            if (!handResult.success)
            {
                Entry.Logger.Warn($"[DrawAndGuessMod] Failed to add selected card {selectedCard.Id.Entry} to hand.");
                return;
            }

            int handOnlyRank = choice.Options.FindIndex(card => ReferenceEquals(card, selectedCard)) + 1;
            Entry.Logger.Info($"[DrawAndGuessMod] Recipient {choice.Recipient.NetId} selected hand-only card {selectedCard.Id.Entry} at AI rank {handOnlyRank}; card played by {Owner.NetId}.");
            return;
        }

        CardPileAddResult deckResult = await CardPileCmd.Add(selectedCard, PileType.Deck);
        if (!deckResult.success)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to add selected card {selectedCard.Id.Entry} to deck.");
            return;
        }

        CardModel addedDeckCard = deckResult.cardAdded;
        CardModel handCard = choice.CombatState.CloneCard(addedDeckCard);
        handCard.DeckVersion = addedDeckCard;
        await CardPileCmd.Add(handCard, PileType.Hand);
        CardCmd.PreviewCardPileAdd(deckResult, 2f);
        int selectedRank = choice.Options.FindIndex(card => ReferenceEquals(card, selectedCard)) + 1;
        Entry.Logger.Info($"[DrawAndGuessMod] Recipient {choice.Recipient.NetId} selected {selectedCard.Id.Entry} at AI rank {selectedRank}; card played by {Owner.NetId}.");
    }

    private sealed record PendingBlankChoice(
        Player Recipient,
        DrawingResult Drawing,
        uint SessionId,
        string? MemorialArtworkId,
        ICombatState CombatState,
        List<CardModel> Options);

    protected override void OnUpgrade()
    {
        EnergyCost.UpgradeBy(-1);
    }
}
