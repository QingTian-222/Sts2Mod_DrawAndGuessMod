using System.Collections.Generic;
using System.Linq;
using DrawAndGuessMod.Scripts.Networking;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.RunData;

namespace DrawAndGuessMod.Scripts.State;

internal static class DrawingPaletteStore
{
    public const int Capacity = 11;
    private static PlayerRunSavedData<DrawingPalettePlayerState>? _savedData;

    public static void Register()
    {
        _savedData = RitsuLibFramework.GetRunSavedDataStore(Entry.ModId).RegisterPerPlayer(
            "drawing_palette",
            () => new DrawingPalettePlayerState(),
            new RunSavedDataOptions
            {
                SchemaVersion = 1,
                WritePolicy = RunSavedDataWritePolicy.WhenNonDefault
            });
    }

    public static IReadOnlyList<Color> GetColors(Player player)
    {
        if (player.RunState is not RunState || _savedData == null)
        {
            return [];
        }

        DrawingPalettePlayerState data = _savedData.Get(player);
        data.Colors ??= new List<uint>();
        if (data.ColorEncodingVersion < 1)
        {
            _savedData.Modify(player, MigrateLegacyColors);
            data = _savedData.Get(player);
        }

        return data.Colors
            .Take(Capacity)
            .Select(DrawingCommand.UnpackRgb)
            .ToList();
    }

    public static bool TryRemember(Player player, Color color)
    {
        if (player.RunState is not RunState || _savedData == null)
        {
            return false;
        }

        uint packedColor = DrawingCommand.PackRgb(color);
        _savedData.Modify(player, data =>
        {
            data.Colors ??= new List<uint>();
            if (data.ColorEncodingVersion < 1)
            {
                MigrateLegacyColors(data);
            }

            data.Colors.Insert(0, packedColor);
            if (data.Colors.Count > Capacity)
            {
                data.Colors.RemoveRange(Capacity, data.Colors.Count - Capacity);
            }
        });
        return true;
    }

    private static void MigrateLegacyColors(DrawingPalettePlayerState data)
    {
        data.Colors ??= new List<uint>();
        for (int index = 0; index < data.Colors.Count; index++)
        {
            uint oldRgb = data.Colors[index];
            data.Colors[index] = 0xFF000000u | oldRgb;
        }

        data.ColorEncodingVersion = 1;
    }
}
