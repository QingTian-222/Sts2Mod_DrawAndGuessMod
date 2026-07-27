using System.Collections.Generic;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Guess;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
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

    public Blank() : base(1, CardType.Skill, CardRarity.Rare, TargetType.AnyPlayer)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        Player recipient = cardPlay.Target?.Player ?? Owner;
        uint sessionId = DrawGuessSession.GetSessionId(choiceContext);
        await CoopDrawFlow.RunAsync(this, choiceContext, recipient, sessionId);
    }

    protected override void OnUpgrade()
    {
        EnergyCost.UpgradeBy(-1);
    }
}
