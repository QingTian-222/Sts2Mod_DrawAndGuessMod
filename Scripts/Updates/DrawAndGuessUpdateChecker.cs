using System.Reflection;
using System.Text.Json;
using STS2RitsuLib;
using STS2RitsuLib.Updates;

namespace DrawAndGuessMod.Scripts.Updates;

internal static class DrawAndGuessUpdateChecker
{
    private const string UnknownVersion = "0.0.0";
    private static readonly Uri ManifestUri = new(
        "https://qingtian-222.github.io/Sts2Mod_DrawAndGuessMod/update.json");
    private static readonly Uri ReleasePageUri = new(
        "https://github.com/QingTian-222/Sts2Mod_DrawAndGuessMod/releases");

    public static void Register(Assembly assembly)
    {
        try
        {
            ModUpdateCheckOptions options = new()
            {
                ModId = Entry.ModId,
                DisplayName = "Draw & Guess / 你画瓦猜",
                CurrentVersion = ReadInstalledVersion(assembly),
                ManifestUri = ManifestUri,
                ReleasePageUri = ReleasePageUri,
            };

            RitsuLibFramework.RegisterModUpdateCheck(options);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to register update checker: {ex}");
        }
    }

    private static string ReadInstalledVersion(Assembly assembly)
    {
        string? assemblyDirectory = Path.GetDirectoryName(assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            string manifestPath = Path.Combine(assemblyDirectory, $"{Entry.ModId}.json");
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (document.RootElement.TryGetProperty("version", out JsonElement versionElement))
                {
                    string? version = versionElement.GetString();
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        return version.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn(
                    $"[DrawAndGuessMod] Failed to read installed version from '{manifestPath}': {ex.Message}");
            }
        }

        return UnknownVersion;
    }
}
