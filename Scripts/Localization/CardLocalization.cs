using System;
using System.Collections.Generic;
using DrawAndGuessMod.Scripts.Cards;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Localization;

internal static class CardLocalization
{
    private const string GeneratedId = "DRAW_AND_GUESS_MOD_CARD_BLANK";
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
                Add(values, ModelDb.GetId(typeof(Blank)).Entry);
            }
            catch
            {
            }

            LocManager.Instance.GetTable("cards").MergeWith(values);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to install localization: {ex.Message}");
        }
    }

    private static void Add(IDictionary<string, string> values, string id)
    {
        values[id + ".title"] = "空白";
        values[id + ".description"] = "绘制一张卡面，瓦库会把它猜测的{IfUpgraded:show:[gold]升级过的[/gold]}卡牌加入你的手牌和牌组。";
    }
}

[HarmonyPatch(typeof(LocManager), nameof(LocManager.Initialize))]
internal static class LocManagerInitializePatch
{
    private static void Postfix()
    {
        CardLocalization.Install();
    }
}
