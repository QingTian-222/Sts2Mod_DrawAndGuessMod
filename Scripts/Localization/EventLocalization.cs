using System;
using System.Collections.Generic;
using DrawAndGuessMod.Scripts.Events;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Localization;

internal static class EventLocalization
{
    private const string GeneratedId = "DRAW_AND_GUESS_MOD_EVENT_VAKUUS_INFINITE_GALLERY";
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
            try
            {
                Add(values, ModelDb.GetId(typeof(VakuusInfiniteGallery)).Entry);
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
            "潦草就急",
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
            "目标是[gold]{Target}[/gold]，你选择了[gold]{Chosen}[/gold]。这样不对！限时挑战到此结束。\n\n瓦库却没有立刻赶你走。他将本次成功的画作逐一装订成书，整齐地摆到你面前。",
            "The target was [gold]{Target}[/gold], but you chose [gold]{Chosen}[/gold]. The timed challenge is over.\n\nYet VAKUU does not send you away. He binds every successful drawing into a book and arranges the collection before you.");
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
            "阅读并离开",
            "Read and Leave");
        values[$"{id}.pages.RESULT.options.TAKE_AND_LEAVE.description"] = ModText.Get(
            "从本次成功画出的[gold]{SuccessCount}[/gold]张牌中选择一张加入牌组。",
            "Choose one of your [gold]{SuccessCount}[/gold] successful drawings to add to your deck.");
        values[$"{id}.selectionScreenPrompt"] = ModText.Get(
            "从瓦库装订的画册中选择一本带走。",
            "Choose one of VAKUU's bound volumes to take with you.");
        values[$"{id}.pages.TAKE_REWARD.description"] = ModText.Get(
            "你挑中一本，在画廊角落的椅子上坐下，安静地翻阅起来。\n\n你不禁思考，这么多画，仿佛能装下一个大图书馆。",
            "You choose a volume, settle into a chair in the corner, and read in silence.\n\nYou begin to wonder whether the bond between pictures and cards exists elsewhere in the Spire.");
        values[$"{id}.pages.LEAVE.description"] = ModText.Get(
            "身后的画框渐渐隐入黑暗。瓦库没有挽留，只是继续端详着你留下的画。",
            "The frames behind you fade into darkness. VAKUU does not stop you; he simply keeps studying the drawings you left behind.");
        values[$"{id}.pages.EXHAUSTED.description"] = ModText.Get(
            "你掀开最后一块空白画布，发现背面藏着一行小字：\n\n[jitter][purple]“有个角色已经把世界上的牌都画完了。你猜他是谁。”[/purple][/jitter]",
            "You lift the final blank canvas and find a tiny line hidden on its back:\n\n[jitter][purple]\"You have drawn every card in the world. Now it is VAKUU's turn to guess who you are.\"[/purple][/jitter]\n\nFor the first time, VAKUU does not answer immediately.");
    }
}
