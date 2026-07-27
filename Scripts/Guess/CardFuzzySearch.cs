using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
    private static object? _cacheLocaleKey;

    private sealed record CardEntry(string CardId, string Title, string NormalizedTitle, CardModel Card);

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

    private static List<CardEntry> EnsureIndex(Player owner)
    {
        // 以本地化单例实例作为缓存键：语言切换后 LocManager 会重新初始化，实例随之更换。
        object? localeKey = LocManager.Instance;
        if (_cache != null && ReferenceEquals(_cacheLocaleKey, localeKey))
        {
            return _cache;
        }

        List<CardEntry> entries = new();
        foreach (CardModel card in ModelDb.AllCards)
        {
            if (card == null || card.Id.Entry.Length == 0)
            {
                continue;
            }

            // 与 AI 识别候选保持同一口径的可猜牌池过滤。
            if (!DrawAndGuessSettings.IncludeMultiplayerCards &&
                card.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly)
            {
                continue;
            }

            if (DrawAndGuessSettings.CardPoolScope == GuessCardPoolScope.CurrentCharacter &&
                owner.Character != null &&
                !owner.Character.CardPool.AllCardIds.Contains(card.Id))
            {
                continue;
            }

            string title = TryGetLocTitle(card.Id.Entry) ?? card.Id.Entry;
            entries.Add(new CardEntry(card.Id.Entry, title, Normalize(title), card));
        }

        _cache = entries;
        _cacheLocaleKey = localeKey;
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
