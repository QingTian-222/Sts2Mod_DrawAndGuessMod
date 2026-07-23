using MegaCrit.Sts2.Core.Localization;

namespace DrawAndGuessMod.Scripts.Localization;

internal static class ModText
{
    public static bool IsChinese
    {
        get
        {
            string? language = LocManager.Instance?.Language;
            return language is "zhs" or "zht";
        }
    }

    public static string Get(string simplifiedChinese, string english)
    {
        return IsChinese ? simplifiedChinese : english;
    }
}
