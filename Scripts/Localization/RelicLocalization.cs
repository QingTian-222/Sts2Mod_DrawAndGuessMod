using System;
using System.Collections.Generic;
using DrawAndGuessMod.Scripts.Relics;
using DrawAndGuessMod.Scripts.RestSite;
using DrawAndGuessMod.Scripts.Ui;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Localization;

internal static class RelicLocalization
{
    private const string GeneratedRelicId = "DRAW_AND_GUESS_MOD_RELIC_DEATH_NOTE";
    private const string GeneratedMemorialSketchbookId =
        "DRAW_AND_GUESS_MOD_RELIC_MEMORIAL_SKETCHBOOK";
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
            Dictionary<string, string> relicValues = new();
            AddDeathNote(relicValues, GeneratedRelicId);
            try
            {
                AddDeathNote(relicValues, ModelDb.GetId(typeof(DeathNote)).Entry);
            }
            catch
            {
            }

            AddMemorialSketchbook(relicValues, GeneratedMemorialSketchbookId);
            try
            {
                AddMemorialSketchbook(
                    relicValues,
                    ModelDb.GetId(typeof(MemorialSketchbook)).Entry);
            }
            catch
            {
            }

            relicValues[DeathNoteCardViewer.InfoTextKey] = ModText.Get(
                "被死亡绘本消除的卡牌",
                "Cards erased by Death Sketchbook");
            relicValues[MemorialSketchbookCardViewer.InfoTextKey] = ModText.Get(
                "限时挑战连胜的卡牌",
                "Cards from the timed challenge streak");
            LocManager.Instance.GetTable("relics").MergeWith(relicValues);

            Dictionary<string, string> optionValues = new()
            {
                [DeathNoteRestSiteOption.TitleKey] =
                    ModText.Get("绘画", "Draw"),
                [DeathNoteRestSiteOption.DescriptionKey] =
                    ModText.Get(
                        "绘制一张卡面。它会从本局游戏中[red]彻底消失[/red]。（无论它在哪里。）",
                        "Draw a card illustration. It vanishes from the game entirely. (No matter where it is.)")
            };
            LocManager.Instance.GetTable("rest_site_ui").MergeWith(optionValues);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to install Death Sketchbook localization: {ex.Message}");
        }
    }

    private static void AddDeathNote(IDictionary<string, string> values, string id)
    {
        values[id + ".title"] = ModText.Get("死亡绘本", "Death Sketchbook");
        values[id + ".description"] = ModText.Get(
            "你可以在火堆[gold]绘画[/gold]，[gold]绘画[/gold]的卡牌会从本局游戏中[jitter][red]彻底消失[/red][/jitter]。\n拾起时，获得一张随机[red]诅咒[/red]。",
            "You can [gold]Draw[/gold] at Rest Sites. Cards you draw disappear completely from this run. Upon pickup, obtain a random [red]Curse[/red].");
        values[id + ".flavor"] = ModText.Get(
            "听说在另一个世界，也有一本类似的笔记本。",
            "Heard that in another world, there is also a similar notebook.");
    }

    private static void AddMemorialSketchbook(
        IDictionary<string, string> values,
        string id)
    {
        values[id + ".title"] = ModText.Get("纪念绘本", "Memorial Sketchbook");
        values[id + ".description"] = ModText.Get(
            "这只是一本纪念册。",
            "This is just a memorial album.");
        values[id + ".flavor"] = "";
    }
}
