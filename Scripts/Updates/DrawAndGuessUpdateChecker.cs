using System.Reflection;
using System.Text.Json;
using DrawAndGuessMod.Scripts.Localization;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
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
    private static Assembly? _pendingAssembly;
    private static bool _registered;

    public static void Register(Assembly assembly)
    {
        _pendingAssembly = assembly;
        TryRegister();
    }

    internal static void TryRegister()
    {
        if (_registered || _pendingAssembly == null || LocManager.Instance == null)
        {
            return;
        }

        try
        {
            ModUpdateCheckOptions options = new()
            {
                ModId = Entry.ModId,
                DisplayName = ModText.Get("DRAW_AND_GUESS_MOD.UPDATE_CHECKER.DISPLAY_NAME"),
                CurrentVersion = ReadInstalledVersion(_pendingAssembly),
                ManifestUri = ManifestUri,
                ReleasePageUri = ReleasePageUri,
            };

            RitsuLibFramework.RegisterModUpdateCheck(options);
            _registered = true;
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

[HarmonyPatch(typeof(LocManager), nameof(LocManager.Initialize))]
internal static class DrawAndGuessUpdateCheckerLocalizationPatch
{
    private static void Postfix()
    {
        DrawAndGuessUpdateChecker.TryRegister();
    }
}
