using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.State;

internal sealed class DrawingHistoryEntry
{
    /// <summary>Unique per drawing within a completed run and drawing session.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>"card" for a chosen card, "relic" for a relic-appraisal work.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The card/relic id entry that was chosen when the drawing was finished.</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>Relic appraisal work title (null for card entries).</summary>
    public string? WorkTitle { get; set; }

    /// <summary>User-facing artwork name. Defaults to its creation date.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>PNG file name inside the history directory.</summary>
    public string FileName { get; set; } = string.Empty;

    public long Timestamp { get; set; }
}

/// <summary>
/// Persists every finished drawing as a PNG plus a JSON index inside a dedicated
/// subdirectory of the mod's user-data folder: user://mods/DrawAndGuessMod/history/.
/// Each entry records which card (or relic) was chosen at the time. The drawing
/// key deduplicates by (run, drawer, session) so re-resolving the same drawing
/// never produces a duplicate entry, while separate runs retain their own works.
/// </summary>
internal static class DrawingHistoryStore
{
    private const string HistoryDirName = "history";
    private const string IndexFileName = "index.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static List<DrawingHistoryEntry>? _cachedEntries;
    private static long _cachedIndexWriteTicks = long.MinValue;

    private static string HistoryDirectory
    {
        get
        {
            string root = ProjectSettings.GlobalizePath("user://mods/DrawAndGuessMod");
            return Path.Combine(root, HistoryDirName);
        }
    }

    private static string IndexPath => Path.Combine(HistoryDirectory, IndexFileName);

    private static string BuildKey(IRunState runState, ulong ownerNetId, uint sessionId)
    {
        long startTime = RunManager.Instance.ToSave(null).StartTime;
        return $"{startTime:X16}:{runState.Rng.StringSeed}:{ownerNetId:X16}:{sessionId:X8}";
    }

    /// <summary>
    /// Absolute path of the folder that stores the history PNGs and index.json.
    /// Creates the folder if it does not exist yet so callers can always open it.
    /// </summary>
    public static string EnsureHistoryDirectory()
    {
        string directory = HistoryDirectory;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to create history directory: {ex.Message}");
        }

        return directory;
    }

    public static void RecordCard(
        IRunState runState,
        ulong ownerNetId,
        uint sessionId,
        CardModel card,
        byte[] pngBytes)
    {
        try
        {
            if (pngBytes.Length == 0)
            {
                return;
            }

            Record(BuildKey(runState, ownerNetId, sessionId), "card", card.Id.Entry, null, pngBytes);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to record card drawing history for {card.Id.Entry}: {ex}");
        }
    }

    public static void RecordRelic(
        IRunState runState,
        ulong ownerNetId,
        uint sessionId,
        RelicModel relic,
        string workTitle,
        byte[] pngBytes)
    {
        try
        {
            if (pngBytes.Length == 0)
            {
                return;
            }

            Record(BuildKey(runState, ownerNetId, sessionId), "relic", relic.Id.Entry, workTitle, pngBytes);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to record relic drawing history for {relic.Id.Entry}: {ex}");
        }
    }

    public static IReadOnlyList<DrawingHistoryEntry> GetEntries()
    {
        try
        {
            long indexWriteTicks = GetIndexWriteTicks();
            if (_cachedEntries != null && _cachedIndexWriteTicks == indexWriteTicks)
            {
                return CloneEntries(_cachedEntries);
            }

            List<DrawingHistoryEntry> entries = ReadEntries(HistoryDirectory);
            if (EnsureNames(entries) || IndexUsesEscapedUnicode())
            {
                WriteEntries(HistoryDirectory, entries);
            }
            CacheEntries(entries);
            return CloneEntries(entries);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to read drawing history: {ex.Message}");
            return [];
        }
    }

    public static byte[]? LoadPng(string fileName)
    {
        try
        {
            string path = GetPngPath(HistoryDirectory, fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to load history drawing {fileName}: {ex.Message}");
            return null;
        }
    }

    public static bool TryRename(
        string key,
        string requestedName,
        out string normalizedName,
        out string savedFileName)
    {
        normalizedName = NormalizeDisplayName(requestedName);
        savedFileName = string.Empty;
        if (normalizedName.Length == 0)
        {
            return false;
        }

        try
        {
            string directory = HistoryDirectory;
            List<DrawingHistoryEntry> entries = ReadEntries(directory);
            DrawingHistoryEntry? entry = entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.Ordinal));
            if (entry == null)
            {
                return false;
            }

            string oldFileName = entry.FileName;
            entry.Name = normalizedName;
            entry.FileName = BuildPngFileName(entries, entry, directory);
            string oldPath = GetPngPath(directory, oldFileName);
            string newPath = GetPngPath(directory, entry.FileName);
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
            {
                File.Move(oldPath, newPath, overwrite: true);
            }

            WriteEntries(directory, entries);
            savedFileName = entry.FileName;
            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to rename history drawing '{key}': {ex.Message}");
            return false;
        }
    }

    public static bool TryReplacePng(string key, byte[] pngBytes)
    {
        if (!IsPng(pngBytes))
        {
            return false;
        }

        try
        {
            string directory = HistoryDirectory;
            List<DrawingHistoryEntry> entries = ReadEntries(directory);
            DrawingHistoryEntry? entry = entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.Ordinal));
            if (entry == null)
            {
                return false;
            }

            EnsureName(entry);
            Directory.CreateDirectory(directory);
            if (string.IsNullOrWhiteSpace(entry.FileName) ||
                !entry.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                entry.FileName = BuildPngFileName(entries, entry, directory);
            }

            File.WriteAllBytes(GetPngPath(directory, entry.FileName), pngBytes);
            WriteEntries(directory, entries);
            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to replace history drawing '{key}': {ex.Message}");
            return false;
        }
    }

    public static bool TryDelete(string key)
    {
        try
        {
            string directory = HistoryDirectory;
            List<DrawingHistoryEntry> entries = ReadEntries(directory);
            DrawingHistoryEntry? entry = entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.Ordinal));
            if (entry == null)
            {
                return false;
            }

            entries.Remove(entry);
            string pngPath = GetPngPath(directory, entry.FileName);
            if (File.Exists(pngPath) && !TryMoveToRecycleBin(pngPath))
            {
                return false;
            }

            WriteEntries(directory, entries);
            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to delete history drawing '{key}': {ex.Message}");
            return false;
        }
    }

    private static void Record(string key, string kind, string targetId, string? workTitle, byte[] pngBytes)
    {
        string directory = HistoryDirectory;
        Directory.CreateDirectory(directory);

        List<DrawingHistoryEntry> entries = ReadEntries(directory);
        DrawingHistoryEntry? existing = entries.FirstOrDefault(
            entry => string.Equals(entry.Key, key, StringComparison.Ordinal));
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (existing != null)
        {
            existing.Kind = kind;
            existing.TargetId = targetId;
            existing.WorkTitle = workTitle;
            existing.Timestamp = timestamp;
            EnsureName(existing);
            string oldPath = GetPngPath(directory, existing.FileName);
            existing.FileName = BuildPngFileName(entries, existing, directory);
            string newPath = GetPngPath(directory, existing.FileName);
            File.WriteAllBytes(newPath, pngBytes);
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }
        }
        else
        {
            DrawingHistoryEntry entry = new()
            {
                Key = key,
                Kind = kind,
                TargetId = targetId,
                WorkTitle = workTitle,
                Timestamp = timestamp,
                Name = DefaultName(timestamp)
            };
            entry.FileName = BuildPngFileName(entries, entry, directory);
            entries.Add(entry);
            File.WriteAllBytes(GetPngPath(directory, entry.FileName), pngBytes);
        }

        WriteEntries(directory, entries);
    }

    private static List<DrawingHistoryEntry> ReadEntries(string directory)
    {
        string path = Path.Combine(directory, IndexFileName);
        if (!File.Exists(path))
        {
            return new List<DrawingHistoryEntry>();
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<DrawingHistoryEntry>>(json)
                   ?? new List<DrawingHistoryEntry>();
        }
        catch
        {
            return new List<DrawingHistoryEntry>();
        }
    }

    private static void WriteEntries(string directory, List<DrawingHistoryEntry> entries)
    {
        string json = JsonSerializer.Serialize(entries, JsonOptions);
        string path = Path.Combine(directory, IndexFileName);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
        CacheEntries(entries);
    }

    private static long GetIndexWriteTicks()
    {
        return File.Exists(IndexPath) ? File.GetLastWriteTimeUtc(IndexPath).Ticks : 0L;
    }

    private static void CacheEntries(IEnumerable<DrawingHistoryEntry> entries)
    {
        _cachedEntries = CloneEntries(entries).ToList();
        _cachedIndexWriteTicks = GetIndexWriteTicks();
    }

    private static IReadOnlyList<DrawingHistoryEntry> CloneEntries(IEnumerable<DrawingHistoryEntry> entries)
    {
        return entries.Select(entry => new DrawingHistoryEntry
        {
            Key = entry.Key,
            Kind = entry.Kind,
            TargetId = entry.TargetId,
            WorkTitle = entry.WorkTitle,
            Name = entry.Name,
            FileName = entry.FileName,
            Timestamp = entry.Timestamp
        }).ToList();
    }

    private static bool IndexUsesEscapedUnicode()
    {
        try
        {
            return File.Exists(IndexPath) &&
                   File.ReadAllText(IndexPath).Contains("\\u", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool EnsureNames(IEnumerable<DrawingHistoryEntry> entries)
    {
        bool changed = false;
        foreach (DrawingHistoryEntry entry in entries)
        {
            changed |= EnsureName(entry);
        }
        return changed;
    }

    private static bool EnsureName(DrawingHistoryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Name))
        {
            return false;
        }

        entry.Name = DefaultName(entry.Timestamp);
        return true;
    }

    private static string DefaultName(long timestamp)
    {
        DateTimeOffset moment = timestamp > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime()
            : DateTimeOffset.Now;
        return moment.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static string NormalizeDisplayName(string value)
    {
        return value.Trim().Length > 80 ? value.Trim()[..80] : value.Trim();
    }

    private static string BuildPngFileName(
        IReadOnlyCollection<DrawingHistoryEntry> entries,
        DrawingHistoryEntry entry,
        string directory)
    {
        string baseName = Path.GetFileNameWithoutExtension(entry.Name);
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            baseName = baseName.Replace(invalid, '_');
        }

        baseName = baseName.Trim().Trim('.');
        if (baseName.Length == 0)
        {
            baseName = "artwork";
        }

        string fileName = baseName + ".png";
        int suffix = 2;
        while (entries.Any(other => !ReferenceEquals(other, entry) &&
                                   string.Equals(other.FileName, fileName, StringComparison.OrdinalIgnoreCase)) ||
               (File.Exists(GetPngPath(directory, fileName)) &&
                !string.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
        {
            fileName = $"{baseName} ({suffix++}).png";
        }

        return fileName;
    }

    private static string GetPngPath(string directory, string fileName)
    {
        return Path.Combine(directory, Path.GetFileName(fileName));
    }

    private static bool TryMoveToRecycleBin(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Delete(path);
            return true;
        }

        ShellFileOperation operation = new()
        {
            Function = ShellFileOperationDelete,
            From = path + "\0\0",
            Flags = ShellFileOperationAllowUndo |
                    ShellFileOperationNoConfirmation |
                    ShellFileOperationSilent
        };
        return ShFileOperation(ref operation) == 0 && !operation.Aborted;
    }

    private static bool IsPng(byte[] pngBytes)
    {
        if (pngBytes.Length == 0)
        {
            return false;
        }

        Image image = new();
        return image.LoadPngFromBuffer(pngBytes) == Error.Ok;
    }

    private const uint ShellFileOperationDelete = 0x0003;
    private const ushort ShellFileOperationAllowUndo = 0x0040;
    private const ushort ShellFileOperationNoConfirmation = 0x0010;
    private const ushort ShellFileOperationSilent = 0x0004;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ShellFileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        public bool Aborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [System.Runtime.InteropServices.DllImport(
        "shell32.dll",
        EntryPoint = "SHFileOperationW",
        ExactSpelling = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int ShFileOperation(ref ShellFileOperation operation);
}
