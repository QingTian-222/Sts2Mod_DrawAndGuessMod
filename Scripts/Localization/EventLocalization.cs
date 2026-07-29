using System;
using System.Collections.Generic;
using DrawAndGuessMod.Scripts.Events;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Localization;

internal static class EventLocalization
{
    private const string GeneratedId = "DRAW_AND_GUESS_MOD_EVENT_VAKUUS_INFINITE_GALLERY";
    private const string RelicAuctionGeneratedId = "DRAW_AND_GUESS_MOD_EVENT_RELIC_AUCTION";
    private static bool _subscribed;

    public static void Install()
    {
        if (LocManager.Instance == null)
        {
            return;
        }

        EnsureLocalization();
        if (_subscribed)
        {
            return;
        }

        LocString.SubscribeToLocaleChange(EnsureLocalization);
        _subscribed = true;
    }

    private static void EnsureLocalization()
    {
        try
        {
            Dictionary<string, string> values = new();
            Add(values, GeneratedId);
            AddRelicAuction(values, RelicAuctionGeneratedId);
            try
            {
                Add(values, ModelDb.GetId(typeof(VakuusInfiniteGallery)).Entry);
            }
            catch
            {
            }
            try
            {
                AddRelicAuction(values, ModelDb.GetId(typeof(RelicAuction)).Entry);
            }
            catch
            {
            }
            LocManager.Instance.GetTable("events").MergeWith(values);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to install event localization: {ex.Message}");
        }
    }

    private static void Add(IDictionary<string, string> values, string id)
    {
        values[$"{id}.title"] = ModText.Get("瓦库的无限画廊", "VAKUU's Infinite Gallery");
        values[$"{id}.pages.INITIAL.description"] = ModText.Get(
            "瓦库指向一条望不到尽头的画廊。每一块空白画框，都在等待你为一张卡牌留下新的面貌。",
            "VAKUU gestures toward a gallery with no visible end. Every empty frame waits for you to give a card a new face.");
        values[$"{id}.pages.INITIAL.options.TIMED.title"] = ModText.Get(
            "潦草急就",
            "Timed Drawing");
        values[$"{id}.pages.INITIAL.options.TIMED.description"] = ModText.Get(
            "限时60秒作画。每次成功获得[gold]60[/gold]金币。",
            "Draw within [gold]60 seconds[/gold]. Each success grants [gold]60[/gold] Gold.");
        values[$"{id}.pages.INITIAL.options.STANDARD.title"] = ModText.Get(
            "冷静头脑",
            "Drawing");
        values[$"{id}.pages.INITIAL.options.STANDARD.description"] = ModText.Get(
            "不限时间。第一次成功获得[gold]60[/gold]金币。",
            "No time limit. Your first success grants [gold]60[/gold] Gold.");
        values[$"{id}.pages.SUCCESS_TIMED.description"] = ModText.Get(
            "目标是[gold]{Target}[/gold]，你选择了[gold]{Chosen}[/gold]。这样才对！你获得了[gold]{Reward}[/gold]金币。\n\n你已经成功完成了[gold]{SuccessCount}[/gold]幅画。\n\n{Reaction}",
            "The target was [gold]{Target}[/gold], and you chose [gold]{Chosen}[/gold]. Correct! You earned [gold]{Reward}[/gold] Gold.\n\nYou have successfully completed [gold]{SuccessCount}[/gold] drawings.\n\n{Reaction}");
        values[$"{id}.pages.SUCCESS_REWARD.description"] = ModText.Get(
            "目标是[gold]{Target}[/gold]，你选择了[gold]{Chosen}[/gold]。这样才对！你获得了[gold]{Reward}[/gold]金币。",
            "The target was [gold]{Target}[/gold], and you chose [gold]{Chosen}[/gold]. Your drawing passes VAKUU's scrutiny, earning [gold]{Reward}[/gold] Gold.");
        values[$"{id}.pages.SUCCESS.description"] = ModText.Get(
            "目标是[gold]{Target}[/gold]，你选择了[gold]{Chosen}[/gold]。画作通过了瓦库的审视，这样才对！",
            "The target was [gold]{Target}[/gold], and you chose [gold]{Chosen}[/gold]. Your drawing passes VAKUU's scrutiny.");
        values[$"{id}.pages.FAIL_TIMED.description"] = ModText.Get(
            "目标是[gold]{Target}[/gold]，你选择了[gold]{Chosen}[/gold]。这样不对！限时挑战到此结束。",
            "The target was [gold]{Target}[/gold], but you chose [gold]{Chosen}[/gold]. VAKUU shakes his head. The timed challenge is over.");
        values[$"{id}.pages.FAIL_TIMED_REWARD.description"] = ModText.Get(
            "目标是[gold]{Target}[/gold]，你选择了[gold]{Chosen}[/gold]。这样不对！限时挑战到此结束。\n\n瓦库却没有立刻赶你走。他将本次连胜的画作装订成一本纪念绘本，递到你面前。",
            "The target was [gold]{Target}[/gold], but you chose [gold]{Chosen}[/gold]. The timed challenge is over.\n\nYet VAKUU does not send you away. He binds the drawings from your winning streak into a Memorial Sketchbook and offers it to you.");
        values[$"{id}.pages.FAIL_STANDARD.description"] = ModText.Get(
            "目标是[gold]{Target}[/gold]，你选择了[gold]{Chosen}[/gold]。这幅画没能命中目标，但瓦库允许你再试一次。",
            "The target was [gold]{Target}[/gold], but you chose [gold]{Chosen}[/gold]. The drawing missed its mark, but VAKUU allows another attempt.");
        values[$"{id}.pages.RESULT.options.CONTINUE.title"] = ModText.Get(
            "继续挑战",
            "Continue");
        values[$"{id}.pages.RESULT.options.CONTINUE.description"] = ModText.Get(
            "随机指定另一张卡牌，继续作画。",
            "Receive another random target and keep drawing.");
        values[$"{id}.pages.RESULT.options.LEAVE.title"] = ModText.Get(
            "离开",
            "Leave");
        values[$"{id}.pages.RESULT.options.LEAVE.description"] = ModText.Get(
            "离开无限画廊。",
            "Leave the Infinite Gallery.");
        values[$"{id}.pages.RESULT.options.TAKE_AND_LEAVE.title"] = ModText.Get(
            "收下并离开",
            "Take and Leave");
        values[$"{id}.pages.RESULT.options.TAKE_AND_LEAVE.description"] = ModText.Get(
            "获得[gold]纪念绘本[/gold]。打开它可以查看本次限时挑战连胜的卡牌。",
            "Obtain the [gold]Memorial Sketchbook[/gold]. Open it to view the cards from this timed challenge streak.");
        values[$"{id}.pages.TAKE_REWARD.description"] = ModText.Get(
            "你收下了[gold]纪念绘本[/gold]。那些仓促却珍贵的画作，被整齐地留在每一页中。",
            "You take the [gold]Memorial Sketchbook[/gold]. Those hurried but precious drawings now rest neatly upon its pages.");
        values[$"{id}.pages.LEAVE.description"] = ModText.Get(
            "身后的画框渐渐隐入黑暗。瓦库没有挽留，只是继续端详着你留下的画。",
            "The frames behind you fade into darkness. VAKUU does not stop you; he simply keeps studying the drawings you left behind.");
        values[$"{id}.pages.EXHAUSTED.description"] = ModText.Get(
            "你掀开最后一块空白画布，发现背面藏着一行小字：\n\n[jitter][purple]“有个角色已经把世界上的牌都画完了。你猜他是谁。”[/purple][/jitter]",
            "You lift the final blank canvas and find a tiny line hidden on its back:\n\n[jitter][purple]\"You have drawn every card in the world. Now it is VAKUU's turn to guess who you are.\"[/purple][/jitter]\n\nFor the first time, VAKUU does not answer immediately.");
        values[$"{id}.pages.EXHAUSTED_REWARD.description"] = ModText.Get(
            "你掀开最后一块空白画布，发现背面藏着一行小字：\n\n[jitter][purple]“有个角色已经把世界上的牌都画完了。你猜他是谁。”[/purple][/jitter]\n\n瓦库将本次连胜的画作装订成[gold]纪念绘本[/gold]，交到你的手中。",
            "You lift the final blank canvas and find a tiny line hidden on its back:\n\n[jitter][purple]\"You have drawn every card in the world. Now it is VAKUU's turn to guess who you are.\"[/purple][/jitter]\n\nVAKUU binds the drawings from your streak into a [gold]Memorial Sketchbook[/gold] and places it in your hands.");
    }

    private static void AddRelicAuction(IDictionary<string, string> values, string id)
    {
        values[$"{id}.title"] = ModText.Get("遗物拍卖会", "Relic Auction");
        values[$"{id}.pages.INITIAL.description"] = ModText.Get(
            "白手套的侍者推来一排空展台。主持人将写有遗物名的题签分别塞进你们手中，又铺开几张透明画布。\n\n“请照着题签留下它的模样，再替作品取个好名字。”\n\n槌声响起前，展台只认[gold]画作、作品名与署名[/gold]。帷幕后藏着什么，谁也不能明说。\n\n最后，每个人都能抱走一件作品。至于是不是自己心里那一件，就看落槌前的选择了。\n\n若一轮未尽兴，主持人也乐意再次递来题签——只是下一次入席，价码会更高。今夜的槌声，最多只会为你们响三轮。",
            "White-gloved attendants wheel out a row of empty stands. The auctioneer slips each of you a sealed note bearing a relic's name, then unfurls several transparent canvases.\n\n\"Give the relic on your note a fitting likeness—and give the work a fine name.\"\n\nUntil the hammer falls, the stands acknowledge only the [gold]artwork, its title, and its signature[/gold]. Whatever waits behind the curtain must remain unspoken.\n\nIn the end, everyone leaves with one work. Whether it is the one you hoped for depends on where you place your claim.\n\nShould one round leave you wanting more, the auctioneer will gladly offer fresh commissions—though the next seat always costs a little more. Tonight, the hammer will fall for no more than three rounds.");
        values[$"{id}.pages.INITIAL.options.ENTER.title"] = ModText.Get(
            "支付[gold]{Cost}[/gold]金币：入席",
            "Pay [gold]{Cost}[/gold] Gold: Take a Seat");
        values[$"{id}.pages.INITIAL.options.ENTER.description"] = ModText.Get(
            "每位参与者支付入场费，接过题签，完成一件送拍作品。",
            "Each participant pays the entry fee, takes a sealed commission, and prepares a work for auction.");
        values[$"{id}.pages.INITIAL.options.INSUFFICIENT.title"] = ModText.Get(
            "金币不足（还差 [gold]{Remaining}[/gold]）",
            "Not enough Gold ([gold]{Remaining}[/gold] more required)");
        values[$"{id}.pages.INITIAL.options.INSUFFICIENT.description"] = ModText.Get(
            "入场费必须由所有参与者支付。",
            "Every participant must be able to pay the entry fee.");
        values[$"{id}.pages.DONE.description"] = ModText.Get(
            "槌声落下，帷幕依次掀开。有人欢呼，也有人盯着手里的作品沉默不语。\n\n你抱走了[gold]{Awarded}[/gold]——至少，现在它叫这个名字。",
            "The hammer falls and the curtains lift one by one. Someone cheers; someone else studies the work in their hands in silence.\n\nYou leave carrying [gold]{Awarded}[/gold]—at least, that is what it is called now.");
        values[$"{id}.pages.DONE.options.CONTINUE.title"] = ModText.Get(
            "支付[gold]{Cost}[/gold]金币：再来一轮",
            "Pay [gold]{Cost}[/gold] Gold: Another Round");
        values[$"{id}.pages.DONE.options.CONTINUE.description"] = ModText.Get(
            "展台重新清空，每位参与者领取一张新的题签。",
            "The stands are cleared, and each participant receives a fresh commission.");
        values[$"{id}.pages.DONE.options.INSUFFICIENT.title"] = ModText.Get(
            "金币不足（还差[gold]{Remaining}[/gold]）",
            "Not enough Gold ([gold]{Remaining}[/gold] more required)");
        values[$"{id}.pages.DONE.options.INSUFFICIENT.description"] = ModText.Get(
            "所有参与者都付得起下一轮入场费时，拍卖才会继续。",
            "The auction continues only if every participant can pay the next entry fee.");
        values[$"{id}.pages.DONE.options.SOLD_OUT.title"] = ModText.Get(
            "展台已经空了",
            "The Stands Are Empty");
        values[$"{id}.pages.DONE.options.SOLD_OUT.description"] = ModText.Get(
            "主持人已经拿不出足够的新题签。",
            "The auctioneer has no more commissions to offer everyone.");
        values[$"{id}.pages.DONE.options.LEAVE.title"] = ModText.Get(
            "离席",
            "Leave the Auction");
        values[$"{id}.pages.DONE.options.LEAVE.description"] = ModText.Get(
            "带着拍得的作品离开。",
            "Leave with the works you won.");
        values[$"{id}.pages.LEAVE.description"] = ModText.Get(
            "身后的槌声渐渐安静。主持人收起最后一张题签，向你们欠身致意。",
            "The hammering fades behind you. The auctioneer gathers the final commission and bows as you depart.");
        values[$"{id}.work.title"] = "{Title}";
    }
}
