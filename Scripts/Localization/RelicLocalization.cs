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
                "\u88ab\u6b7b\u4ea1\u7ed8\u672c\u6d88\u9664\u7684\u5361\u724c",
                "Cards erased by Death Sketchbook");
            relicValues[MemorialSketchbookCardViewer.InfoTextKey] = ModText.Get(
                "\u5de6\u952e\u67e5\u770b\uff1b\u52fe\u9009\u300c\u8bbe\u4e3a\u6c38\u4e45\u5361\u9762\u300d\u540e\uff0c\u70b9\u51fb\u753b\u4f5c\u5373\u53ef\u66ff\u6362\u3002",
                "Left-click to inspect. Enable Set as Permanent Artwork, then select a drawing to replace its card art.");
            LocManager.Instance.GetTable("relics").MergeWith(relicValues);

            Dictionary<string, string> optionValues = new()
            {
                [DeathNoteRestSiteOption.TitleKey] = ModText.Get(
                    "\u7ed8\u753b",
                    "Draw"),
                [DeathNoteRestSiteOption.DescriptionKey] = ModText.Get(
                    "\u7ed8\u5236\u4e00\u5f20\u5361\u9762\u3002\u5b83\u4f1a\u4ece\u672c\u5c40\u6e38\u620f\u4e2d[red]\u5f7b\u5e95\u6d88\u5931[/red]\u3002\uff08\u65e0\u8bba\u5b83\u5728\u54ea\u91cc\u3002\uff09",
                    "Draw a card illustration. It vanishes from the game entirely. (No matter where it is.)")
            };
            LocManager.Instance.GetTable("rest_site_ui").MergeWith(optionValues);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to install relic localization: {ex.Message}");
        }
    }

    private static void AddDeathNote(IDictionary<string, string> values, string id)
    {
        values[id + ".title"] = ModText.Get(
            "\u6b7b\u4ea1\u7ed8\u672c",
            "Death Sketchbook");
        values[id + ".description"] = ModText.Get(
            "\u4f60\u53ef\u4ee5\u5728\u706b\u5806[gold]\u7ed8\u753b[/gold]\uff0c[gold]\u7ed8\u753b[/gold]\u7684\u5361\u724c\u4f1a\u4ece\u672c\u5c40\u6e38\u620f\u4e2d[jitter][red]\u5f7b\u5e95\u6d88\u5931[/red][/jitter]\u3002\n\u62fe\u8d77\u65f6\uff0c\u83b7\u5f97\u4e00\u5f20\u968f\u673a[red]\u8bc5\u5492[/red]\u3002",
            "You can [gold]Draw[/gold] at Rest Sites. Cards you draw disappear completely from this run. Upon pickup, obtain a random [red]Curse[/red].");
        values[id + ".flavor"] = ModText.Get(
            "\u542c\u8bf4\u5728\u53e6\u4e00\u4e2a\u4e16\u754c\uff0c\u4e5f\u6709\u4e00\u672c\u7c7b\u4f3c\u7684\u7b14\u8bb0\u672c\u3002",
            "Heard that in another world, there is also a similar notebook.");
    }

    private static void AddMemorialSketchbook(IDictionary<string, string> values, string id)
    {
        values[id + ".title"] = ModText.Get(
            "\u7eaa\u5ff5\u7ed8\u672c",
            "Memorial Sketchbook");
        values[id + ".description"] = ModText.Get(
            "\u4fdd\u5b58\u672c\u5c40\u6240\u6709\u73a9\u5bb6\u7684\u6bcf\u4e00\u5e45\u5361\u724c\u753b\u4f5c\u3002\u7ffb\u9605\u65f6\u53ef\u5c06\u4efb\u610f\u4e00\u9875\u8bbe\u4e3a\u6c38\u4e45\u5361\u9762\u3002",
            "Keeps every card drawing made by every player this run. Browse it to choose permanent card artwork.");
        values[id + ".flavor"] = "";
    }
}
