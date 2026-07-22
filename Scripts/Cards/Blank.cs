using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Ai;
using DrawAndGuessMod.Scripts.State;
using DrawAndGuessMod.Scripts.Ui;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using STS2RitsuLib.Interop.AutoRegistration;

namespace DrawAndGuessMod.Scripts.Cards;

[RegisterCard(typeof(ColorlessCardPool))]
public sealed class Blank : CardModel
{
    public override CardMultiplayerConstraint MultiplayerConstraint => CardMultiplayerConstraint.None;
    public override string PortraitPath => MissingPortraitPath;
    public override CardPoolModel VisualCardPool => ModelDb.CardPool<ColorlessCardPool>();
    public override IEnumerable<CardKeyword> CanonicalKeywords => [CardKeyword.Exhaust];

    public Blank() : base(1, CardType.Skill, CardRarity.Rare, TargetType.Self)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        uint sessionId = choiceContext is GameActionPlayerChoiceContext actionContext
            ? actionContext.Action.Id ?? 0u
            : 0u;
        DrawingResult? drawing = await DrawingScreen.ShowAsync(Owner, sessionId);
        if (drawing == null)
        {
            return;
        }

        List<CardModel> options = drawing.Guess.NearestCards
            .Take(3)
            .Select(candidate => Owner.RunState.CreateCard(candidate, Owner))
            .ToList();
        if (options.Count == 0)
        {
            Entry.Logger.Warn("[DrawAndGuessMod] The classifier returned no card choices.");
            return;
        }

        if (IsUpgraded)
        {
            foreach (CardModel option in options.Where(option => option.IsUpgradable))
            {
                CardCmd.Upgrade(option);
            }
        }

        CardModel? selectedCard = await CardSelectCmd.FromChooseACardScreen(choiceContext, options, Owner);
        if (selectedCard == null)
        {
            return;
        }

        ArtworkStore.Set(Owner.RunState, selectedCard.Id.Entry, drawing.PngBytes);

        ICombatState? combatState = CombatState;
        if (combatState == null)
        {
            Entry.Logger.Warn("[DrawAndGuessMod] Combat ended before the guessed card could be created.");
            return;
        }

        CardPileAddResult deckResult = await CardPileCmd.Add(selectedCard, PileType.Deck);
        if (!deckResult.success)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to add selected card {selectedCard.Id.Entry} to deck.");
            return;
        }

        CardModel addedDeckCard = deckResult.cardAdded;
        CardModel handCard = combatState.CloneCard(addedDeckCard);
        handCard.DeckVersion = addedDeckCard;
        await CardPileCmd.Add(handCard, PileType.Hand);
        CardCmd.PreviewCardPileAdd(deckResult, 2f);
        int selectedRank = options.FindIndex(card => ReferenceEquals(card, selectedCard)) + 1;
        Entry.Logger.Info($"[DrawAndGuessMod] Player selected {selectedCard.Id.Entry} at AI rank {selectedRank}.");
    }

    protected override void OnUpgrade()
    {
        EnergyCost.UpgradeBy(-1);
    }
}
