using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace DrawAndGuessMod.Scripts.GameActions;

/// <summary>
/// “你画我猜”发牌动作：作为同步 GameAction 经 <see cref="ActionQueueSynchronizer"/>
/// 广播到全房，各端在同一行动序列中确定性执行——这是联机下改变牌堆的唯一安全方式
/// （直接在行动队列外调用 CardPileCmd 不会跨端同步，且与校验窗口错位）。
/// </summary>
public sealed class DrawGuessGrantAction : GameAction
{
    private readonly Player _owner;
    private readonly string _cardId;
    private readonly bool _upgraded;

    public DrawGuessGrantAction(Player owner, string cardId, bool upgraded)
    {
        _owner = owner;
        _cardId = cardId;
        _upgraded = upgraded;
    }

    public override GameActionType ActionType => GameActionType.CombatPlayPhaseOnly;
    public override ulong OwnerId => _owner.NetId;

    /// <summary>通过行动队列同步器把发牌动作广播到全房（各端在同一行动序列中确定性执行）。</summary>
    public static void EnqueueGrant(Player owner, string cardId, bool upgraded)
    {
        var action = new DrawGuessGrantAction(owner, cardId, upgraded);
        MegaCrit.Sts2.Core.Runs.RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
        Entry.Logger.Info($"[DrawAndGuessMod][Trace] 发牌动作已入队: {cardId} owner={owner.NetId}");
    }

    protected override async Task ExecuteAction()
    {
        CardModel? template = ModelDb.AllCards.FirstOrDefault(card =>
            string.Equals(card.Id.Entry, _cardId, System.StringComparison.Ordinal));
        if (template == null)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] 裁定卡牌 {_cardId} 在本端卡牌库中不存在。");
            return;
        }

        CardModel selectedCard = _owner.RunState.CreateCard(template, _owner);
        if (_upgraded && selectedCard.IsUpgradable)
        {
            CardCmd.Upgrade(selectedCard);
        }

        MegaCrit.Sts2.Core.Combat.ICombatState? combatState = _owner.Creature.CombatState;
        if (combatState == null)
        {
            Entry.Logger.Warn("[DrawAndGuessMod] Combat ended before the guessed card could be created.");
            return;
        }

        CardPileAddResult deckResult = await CardPileCmd.Add(selectedCard, PileType.Deck);
        if (!deckResult.success)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to add selected card {_cardId} to deck.");
            return;
        }

        CardModel handCard = combatState.CloneCard(deckResult.cardAdded);
        handCard.DeckVersion = deckResult.cardAdded;
        await CardPileCmd.Add(handCard, PileType.Hand);
        CardCmd.PreviewCardPileAdd(deckResult, 2f);
        Entry.Logger.Info($"[DrawAndGuessMod] 你画我猜模式发放 {selectedCard.Id.Entry}。");
    }

    public override INetAction ToNetAction()
    {
        return new NetDrawGuessGrantAction(_cardId, _upgraded);
    }

    public override string ToString()
    {
        return $"DrawGuessGrantAction card={_cardId} upgraded={_upgraded} owner={_owner.NetId}";
    }
}

/// <summary><see cref="DrawGuessGrantAction"/> 的网络载荷。</summary>
public sealed class NetDrawGuessGrantAction : INetAction
{
    public string CardId { get; set; } = string.Empty;
    public bool Upgraded { get; set; }

    // 联机反序列化需要无参构造。
    public NetDrawGuessGrantAction()
    {
    }

    public NetDrawGuessGrantAction(string cardId, bool upgraded)
    {
        CardId = cardId;
        Upgraded = upgraded;
    }

    public GameAction ToGameAction(Player player)
    {
        return new DrawGuessGrantAction(player, CardId, Upgraded);
    }

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(CardId);
        writer.WriteBool(Upgraded);
    }

    public void Deserialize(PacketReader reader)
    {
        CardId = reader.ReadString();
        Upgraded = reader.ReadBool();
    }

    public override string ToString()
    {
        return $"NetDrawGuessGrantAction card={CardId} upgraded={Upgraded}";
    }
}
