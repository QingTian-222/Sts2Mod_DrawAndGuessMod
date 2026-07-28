using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.RestSite;
using DrawAndGuessMod.Scripts.State;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace DrawAndGuessMod.Scripts.Relics;

[RegisterRelic(typeof(SharedRelicPool))]
public sealed class DeathNote : ModRelicTemplate
{
    private const string RelicIconPath = "res://images/death_note_relic.png";
    private const string RelicBigIconPath = "res://images/death_note_relic_big.png";

    public override RelicRarity Rarity => RelicRarity.Ancient;
    public override bool HasUponPickupEffect => true;

    public override RelicAssetProfile AssetProfile => new(
        RelicIconPath,
        RelicIconPath,
        RelicBigIconPath);

    public override bool ShouldAddToDeck(CardModel card)
    {
        return !ErasedCardStore.IsErased(Owner.RunState, card.Id);
    }

    public override Task AfterAddToDeckPrevented(CardModel card)
    {
        if (ErasedCardStore.IsErased(Owner.RunState, card.Id))
        {
            TryFlashForRun(Owner.RunState);
            Entry.Logger.Info(
                $"[DrawAndGuessMod] Death Sketchbook prevented erased card {card.Id.Entry} " +
                $"from entering {card.Owner.NetId}'s deck.");
        }

        return Task.CompletedTask;
    }

    public override async Task AfterObtained()
    {
        List<CardModel> availableCurses = ModelDb.CardPool<CurseCardPool>()
            .GetUnlockedCards(Owner.UnlockState, Owner.RunState.CardMultiplayerConstraint)
            .Where(card => card.CanBeGeneratedByModifiers)
            .OrderBy(card => card.Id)
            .ToList();
        if (availableCurses.Count == 0)
        {
            Entry.Logger.Warn("[DrawAndGuessMod] Death Sketchbook could not find an eligible random curse.");
            return;
        }

        CardModel? curseTemplate = Owner.RunState.Rng.Niche.NextItem(availableCurses);
        if (curseTemplate == null)
        {
            Entry.Logger.Warn("[DrawAndGuessMod] Death Sketchbook rolled an invalid random curse.");
            return;
        }

        CardModel curse = Owner.RunState.CreateCard(curseTemplate, Owner);
        CardPileAddResult result = await CardPileCmd.Add(curse, PileType.Deck);
        if (!result.success)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Death Sketchbook failed to add random curse {curseTemplate.Id.Entry}.");
            return;
        }

        CardCmd.PreviewCardPileAdd(result, 2f);
        Entry.Logger.Info(
            $"[DrawAndGuessMod] Death Sketchbook added random curse {result.cardAdded.Id.Entry} to {Owner.NetId}.");
    }

    public override bool TryModifyRestSiteOptions(Player player, ICollection<RestSiteOption> options)
    {
        if (player != Owner)
        {
            return false;
        }

        Player? designatedOwner = player.RunState.Players.FirstOrDefault(candidate =>
            candidate.Relics.Any(relic => relic is DeathNote));
        if (designatedOwner != Owner)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Multiple Death Sketchbooks detected; only player {designatedOwner?.NetId} " +
                "will receive the drawing option at this rest site.");
            return false;
        }

        options.Add(new DeathNoteRestSiteOption(player));
        return true;
    }

    public override bool TryModifyCardRewardOptions(
        Player player,
        List<CardCreationResult> cardRewardOptions,
        CardCreationOptions creationOptions)
    {
        int removed = cardRewardOptions.RemoveAll(option =>
            ErasedCardStore.IsErased(player.RunState, option.Card.Id));
        return removed > 0;
    }

    public override IEnumerable<CardModel> ModifyMerchantCardPool(
        Player player,
        IEnumerable<CardModel> options)
    {
        return options.Where(card =>
            !ErasedCardStore.IsErased(player.RunState, card.Id));
    }

    internal static DeathNote? FindForRun(IRunState runState)
    {
        return runState.Players
            .SelectMany(player => player.Relics)
            .OfType<DeathNote>()
            .FirstOrDefault();
    }

    internal static bool TryFlashForRun(IRunState runState)
    {
        DeathNote? relic = FindForRun(runState);
        if (relic == null)
        {
            return false;
        }

        try
        {
            relic.Flash();
            return true;
        }
        catch (System.Exception ex)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Death Sketchbook flash failed without blocking card removal: {ex.Message}");
            return false;
        }
    }
}
