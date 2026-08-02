using System.IO;
using Godot;

namespace DrawAndGuessMod.Scripts.Assets;

internal static class DrawAndGuessAssets
{
    public const string GalleryPortraitPath =
        "res://images/events/draw_and_guess_mod_event_vakuus_infinite_gallery.png";
    public const string RelicAppraisalFairPortraitPath =
        "res://images/events/draw_and_guess_mod_event_relic_appraisal_fair.png";
    private static Texture2D? _neowSettingsIcon;

    public static Texture2D? NeowSettingsIcon => _neowSettingsIcon;

    public static void Install()
    {
        LoadPackedImage(GalleryPortraitPath);
        LoadPackedImage(RelicAppraisalFairPortraitPath);
        _neowSettingsIcon = LoadExternalImage("Images", "neow_settings_icon.png");
    }

    private static void LoadPackedImage(string path)
    {
        if (!ResourceLoader.Exists(path))
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Packed image asset not found: {path}");
            return;
        }

        try
        {
            Texture2D? texture = ResourceLoader.Load<Texture2D>(
                path,
                null,
                ResourceLoader.CacheMode.Reuse);
            if (texture == null)
            {
                Entry.Logger.Warn(
                    $"[DrawAndGuessMod] Packed image asset could not be loaded: {path}");
                return;
            }

            Entry.Logger.Info(
                $"[DrawAndGuessMod] Loaded packed image asset: {path}");
        }
        catch (System.Exception ex)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Failed to load packed image asset '{path}': {ex.Message}");
        }
    }

    private static Texture2D? LoadExternalImage(params string[] relativePath)
    {
        try
        {
            string assemblyDirectory = Path.GetDirectoryName(typeof(DrawAndGuessAssets).Assembly.Location) ?? string.Empty;
            string path = Path.Combine([assemblyDirectory, .. relativePath]);
            Image image = new();
            Error error = image.Load(path);
            if (error != Error.Ok)
            {
                Entry.Logger.Warn($"[DrawAndGuessMod] External image asset could not be loaded: {path} ({error})");
                return null;
            }

            Entry.Logger.Info($"[DrawAndGuessMod] Loaded external image asset: {path}");
            return ImageTexture.CreateFromImage(image);
        }
        catch (System.Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to load external image asset: {ex.Message}");
            return null;
        }
    }
}
