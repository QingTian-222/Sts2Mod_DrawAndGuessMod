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
            "展台上没有一件真正的遗物，只有等待落笔的透明画布。\n\n每位参与者会得到一个各不相同的遗物题目。每完成一笔，鉴定器都会公开当前的猜测；只有猜测与题目完全一致，作品才能提交。\n\n提交前，你可以为作品任意命名。拍卖阶段只展示[gold]作品名、作者与画作[/gold]，不会透露真正的遗物——也就是说，你完全可以伪装你的遗物。\n\n所有作品会像多人宝箱中的遗物一样被共同争夺，最终每位参与者都会带走一件，但未必是自己画的那件。",
            "No actual relics sit on the stands—only transparent canvases waiting for a mark.\n\nEach participant receives a different relic as their target. After every completed action, the appraiser reveals its current guess; a work can be submitted only when that guess exactly matches the target.\n\nBefore submission, you may give the work any title you like. During the auction, only the [gold]title, artist, and artwork[/gold] are shown. The actual relic remains hidden—so you are free to disguise your relic.\n\nThe works are contested like relics in a multiplayer treasure chest. Every participant will ultimately leave with one, though not necessarily the one they drew.");
        values[$"{id}.pages.INITIAL.options.ENTER.title"] = ModText.Get(
            "免费参加",
            "Enter for Free");
        values[$"{id}.pages.INITIAL.options.ENTER.description"] = ModText.Get(
            "每人绘制并提交一幅遗物作品，然后参加盲拍。",
            "Each player draws and submits one relic work, then joins the blind auction.");
        values[$"{id}.pages.INITIAL.options.INSUFFICIENT.title"] = ModText.Get(
            "金币不足（还差 [gold]{Remaining}[/gold]）",
            "Not enough Gold ([gold]{Remaining}[/gold] more required)");
        values[$"{id}.pages.INITIAL.options.INSUFFICIENT.description"] = ModText.Get(
            "入场费必须由所有参与者支付。",
            "Every participant must be able to pay the entry fee.");
        values[$"{id}.pages.DONE.description"] = ModText.Get(
            "槌声落下，遮住说明的布幕终于掀开。\n\n你获得了[gold]{Awarded}[/gold]。",
            "The hammer falls, and the cloth hiding the relic's identity is finally lifted.\n\nYou obtained [gold]{Awarded}[/gold].");
    }
}
