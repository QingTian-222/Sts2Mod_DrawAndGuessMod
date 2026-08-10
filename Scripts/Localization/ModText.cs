using System;
using MegaCrit.Sts2.Core.Localization;

namespace DrawAndGuessMod.Scripts.Localization;

internal static class ModText
{
    private const string UiTable = "gameplay_ui";

    public static string Get(string key)
    {
        try
        {
            return new LocString(UiTable, key).GetFormattedText();
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to resolve localization key '{key}': {ex.Message}");
            return key;
        }
    }

    public static string Format(string key, params (string Name, object Value)[] variables)
    {
        try
        {
            LocString text = new(UiTable, key);
            foreach ((string name, object value) in variables)
            {
                text.AddObj(name, value);
            }
            return text.GetFormattedText();
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to format localization key '{key}': {ex.Message}");
            return key;
        }
    }
}
