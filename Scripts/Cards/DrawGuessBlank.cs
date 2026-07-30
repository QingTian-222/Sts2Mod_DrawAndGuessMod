using System.Collections.Generic;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Guess;
using DrawAndGuessMod.Scripts.Networking;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using STS2RitsuLib.Interop.AutoRegistration;

namespace DrawAndGuessMod.Scripts.Cards;

/// <summary>你画我猜（联机）：出牌者作画，其余玩家猜测，票池权重随机裁定后发牌。</summary>
[RegisterCard(typeof(ColorlessCardPool))]
public sealed class DrawGuessBlank : CardModel
{
    public override CardMultiplayerConstraint MultiplayerConstraint => CardMultiplayerConstraint.MultiplayerOnly;
    public override string PortraitPath => MissingPortraitPath;
    public override CardPoolModel VisualCardPool => ModelDb.CardPool<ColorlessCardPool>();
    public override IEnumerable<CardKeyword> CanonicalKeywords => [CardKeyword.Exhaust];

    public DrawGuessBlank() : base(1, CardType.Skill, CardRarity.Rare, TargetType.Self)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        uint sessionId = DrawGuessSession.GetSessionId(choiceContext);
        if (DrawingNetSync.IsMultiplayer)
        {
            // 你画我猜：出牌者即绘画者——仅出牌者所在端在本方法内执行前台绘画流程；
            // 其余端立即返回，观看流程由 DrawGuessSpectatorPatch 在后台独立运行，
            // 发牌由绘画者端入队 DrawGuessGrantAction 经行动队列同步到全房。
            if (LocalContext.IsMe(Owner))
            {
                await DrawGuessSession.RunOwnerAsync(Owner, sessionId, IsUpgraded);
            }

            return;
        }

        // 单机没有其他玩家参与猜测，回退为协作绘画流程（AI 识别 + 三选一）。
        Player recipient = cardPlay.Target?.Player ?? Owner;
        await CoopDrawFlow.RunAsync(this, choiceContext, recipient, sessionId);
    }

    protected override void OnUpgrade()
    {
        EnergyCost.UpgradeBy(-1);
    }
}
