using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DrawAndGuessMod.Scripts.Cards;
using DrawAndGuessMod.Scripts.Config;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Guess;

/// <summary>一条模糊检索命中结果。</summary>
public sealed record CardSearchHit(string CardId, string Title, int Score);

/// <summary>
/// 猜测端本地卡牌库模糊检索：全部匹配在本地语言下完成，
/// 网络层只传输命中的 CardId，规避跨语言本地化导致的同步问题。
/// </summary>
internal static class CardFuzzySearch
{
    private const int MaxHits = 5;
    private static List<CardEntry>? _cache;

    /// <summary>缓存键：复合条件，任意一个变化都会使缓存失效。</summary>
    private static (object? Loc, string? CharId, GuessCardPoolScope Scope, bool IncMulti, string AdvancedExcludeHash)? _cacheKey;

    private sealed record CardEntry(string CardId, string Title, string NormalizedTitle, CardModel Card);

    /// <summary>
    /// 返回当前设置下的合法猜测卡牌集合。
    /// 此方法是以下位置的统一候选来源：模糊搜索、CardId 验证、兜底随机选牌。
    /// </summary>
    public static IReadOnlyList<CardModel> GetEligibleGuessCards(Player owner)
    {
        HashSet<ModelId> advancedExcluded;
        try
        {
            advancedExcluded = DrawAndGuessSettings.GetCardIdsExcludedByAdvancedPoolSettings();
        }
        catch
        {
            advancedExcluded = new HashSet<ModelId>();
        }

        List<CardModel> result = new();
        foreach (CardModel card in ModelDb.AllCards)
        {
            if (card == null || card.Id.Entry.Length == 0)
            {
                continue;
            }

            // 排除 Blank、DrawGuessBlank、Mock、不显示在卡牌库中的内部卡、CardType.None。
            if (card is Blank || card is DrawGuessBlank)
            {
                continue;
            }

            if (!card.ShouldShowInCardLibrary || card.Type == CardType.None)
            {
                continue;
            }

            // 排除高级候选卡池设置中被关闭的卡牌。
            if (advancedExcluded.Contains(card.Id))
            {
                continue;
            }

            // 多人卡牌过滤。
            if (!DrawAndGuessSettings.IncludeMultiplayerCards &&
                card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly)
            {
                continue;
            }

            // 当前角色卡池过滤。
            if (DrawAndGuessSettings.CardPoolScope == GuessCardPoolScope.CurrentCharacter &&
                owner.Character != null &&
                !owner.Character.CardPool.AllCardIds.Contains(card.Id))
            {
                continue;
            }

            result.Add(card);
        }

        return result;
    }

    /// <summary>按输入文本检索卡牌，返回按匹配度排序的前 <see cref="MaxHits"/> 条。</summary>
    public static IReadOnlyList<CardSearchHit> Search(string? rawInput, Player owner)
    {
        string normalized = Normalize(rawInput);
        if (normalized.Length == 0)
        {
            return [];
        }

        List<CardEntry> index = EnsureIndex(owner);
        List<CardSearchHit> hits = new(index.Count);
        foreach (CardEntry entry in index)
        {
            int score = Score(normalized, entry.NormalizedTitle);
            if (score > 0)
            {
                hits.Add(new CardSearchHit(entry.CardId, entry.Title, score));
            }
        }

        return hits
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.CardId, StringComparer.Ordinal)
            .Take(MaxHits)
            .ToList();
    }

    /// <summary>按 CardId 取回该卡的本地化卡名（用于结果展示）。</summary>
    public static string GetTitle(string cardId)
    {
        string? title = TryGetLocTitle(cardId);
        return string.IsNullOrWhiteSpace(title) ? cardId : title!;
    }

    /// <summary>使缓存失效（语言切换或设置变更时调用）。</summary>
    public static void InvalidateCache()
    {
        _cache = null;
        _cacheKey = null;
    }

    private static List<CardEntry> EnsureIndex(Player owner)
    {
        // 复合缓存键：本地化实例、角色 ID、卡池范围、多人卡开关、高级排除摘要。
        object? localeKey = LocManager.Instance;
        string? charId = owner.Character?.Id.ToString();
        GuessCardPoolScope scope = DrawAndGuessSettings.CardPoolScope;
        bool incMulti = DrawAndGuessSettings.IncludeMultiplayerCards;

        // 用排除卡牌数量作为高级设置摘要（轻量；如需精确可改为排序后 ID 拼接）。
        string advancedHash;
        try
        {
            advancedHash = DrawAndGuessSettings.GetCardIdsExcludedByAdvancedPoolSettings().Count.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            advancedHash = "0";
        }

        var currentKey = (localeKey, charId, scope, incMulti, advancedHash);
        if (_cache != null && _cacheKey.HasValue && _cacheKey.Value == currentKey)
        {
            return _cache;
        }

        IReadOnlyList<CardModel> eligible = GetEligibleGuessCards(owner);
        List<CardEntry> entries = new(eligible.Count);
        foreach (CardModel card in eligible)
        {
            string title = TryGetLocTitle(card.Id.Entry) ?? card.Id.Entry;
            entries.Add(new CardEntry(card.Id.Entry, title, Normalize(title), card));
        }

        _cache = entries;
        _cacheKey = currentKey;
        Entry.Logger.Info($"[DrawAndGuessMod] 猜测检索索引构建完成：{entries.Count} 张卡牌。");
        return entries;
    }

    /// <summary>从本地化 cards 表读取 <id>.title；失败时返回 null 走回退。</summary>
    private static string? TryGetLocTitle(string cardId)
    {
        try
        {
            LocTable? table = LocManager.Instance?.GetTable("cards");
            if (table == null)
            {
                return null;
            }

            string key = cardId + ".title";
            if (!table.HasEntry(key))
            {
                return null;
            }

            string raw = table.GetRawText(key);
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>统一小写、去变音符、压缩空白，保证跨语言输入稳定匹配。</summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        StringBuilder builder = new(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsWhiteSpace(c) ? ' ' : c);
        }

        string spaced = builder.ToString().Normalize(NormalizationForm.FormC);
        return string.Join(' ', spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int Score(string input, string title)
    {
        if (title.Length == 0)
        {
            return 0;
        }

        if (string.Equals(input, title, StringComparison.Ordinal))
        {
            return 100;
        }

        string compactTitle = title.Replace(" ", string.Empty);
        string compactInput = input.Replace(" ", string.Empty);
        if (compactTitle.StartsWith(compactInput, StringComparison.Ordinal))
        {
            return 80;
        }

        if (compactTitle.Contains(compactInput, StringComparison.Ordinal))
        {
            return 60;
        }

        return IsSubsequence(compactInput, compactTitle) ? 40 : 0;
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        int index = 0;
        foreach (char c in haystack)
        {
            if (index < needle.Length && needle[index] == c)
            {
                index++;
            }
        }

        return index == needle.Length && needle.Length > 0;
    }
}
