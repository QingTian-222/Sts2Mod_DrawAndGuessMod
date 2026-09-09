using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.State;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Relics;
using STS2RitsuLib.Settings;

namespace DrawAndGuessMod.Scripts.Ui;

/// <summary>
/// Displays profile-wide card and relic artwork favorites. Collection
/// membership and active permanent replacement are deliberately separate:
/// disabling a replacement keeps the artwork available in this page.
/// </summary>
internal static class FavoriteArtworkViewer
{
    private const int ThumbnailWidth = 130;
    private const int ThumbnailHeight = 100;
    private static readonly MethodInfo? CardReloadMethod = AccessTools.Method(typeof(NCard), "Reload");
    private static readonly MethodInfo? RelicReloadMethod = AccessTools.Method(typeof(NRelic), "Reload");
    private static int _rebuildGeneration;

    public static Control BuildSettingsControl(IModSettingsUiActionHost host)
    {
        VBoxContainer root = new()
        {
            Name = "DrawAndGuessMod_FavoriteArtwork",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        root.AddThemeConstantOverride("separation", 10);

        Label hint = new()
        {
            Text = ModText.Get("DRAW_AND_GUESS_MOD.FAVORITE_ARTWORK_VIEWER.ADD_FROM_HISTORY_HINT"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        root.AddChild(hint);

        VBoxContainer entriesContainer = new()
        {
            Name = "DrawAndGuessMod_FavoriteArtworkEntries",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        root.AddChild(entriesContainer);

        root.TreeEntered += () => RebuildEntries(entriesContainer);
        root.VisibilityChanged += () =>
        {
            if (root.IsVisibleInTree())
            {
                RebuildEntries(entriesContainer);
            }
        };

        return root;
    }

    private static void RebuildEntries(VBoxContainer entriesContainer)
    {
        int generation = ++_rebuildGeneration;
        foreach (Node child in entriesContainer.GetChildren())
        {
            entriesContainer.RemoveChild(child);
            child.QueueFree();
        }

        entriesContainer.AddThemeConstantOverride("separation", 10);
        List<FavoriteArtworkItem> artworks = GetFavoriteArtworks()
            .OrderBy(artwork => artwork.DisplayName, StringComparer.CurrentCulture)
            .ThenBy(artwork => artwork.Kind)
            .ToList();
        if (artworks.Count == 0)
        {
            entriesContainer.AddChild(new Label
            {
                Text = ModText.Get("DRAW_AND_GUESS_MOD.FAVORITE_ARTWORK_VIEWER.NO_FAVORITE_ARTWORKS"),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            });
        }
        else
        {
            _ = AddRowsAsync(entriesContainer, artworks, generation);
        }

        RefreshLayout(entriesContainer);
    }

    private static IReadOnlyList<FavoriteArtworkItem> GetFavoriteArtworks()
    {
        List<FavoriteArtworkItem> artworks = MemorialSketchbookStore
            .GetPermanentArtworks()
            .Select(artwork => new FavoriteArtworkItem(
                FavoriteArtworkKind.Card,
                artwork.CardId,
                artwork.ArtworkId,
                artwork.PngBase64,
                artwork.Enabled,
                ResolveCardDisplayName(artwork.CardId)))
            .ToList();
        artworks.AddRange(MemorialSketchbookStore
            .GetPermanentRelicArtworks()
            .Select(artwork => new FavoriteArtworkItem(
                FavoriteArtworkKind.Relic,
                artwork.RelicId,
                artwork.ArtworkId,
                artwork.PngBase64,
                artwork.Enabled,
                ResolveRelicDisplayName(artwork.RelicId))));
        return artworks;
    }

    private static async Task AddRowsAsync(
        VBoxContainer entriesContainer,
        IReadOnlyList<FavoriteArtworkItem> artworks,
        int generation)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        foreach (FavoriteArtworkItem artwork in artworks)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (generation != _rebuildGeneration || !GodotObject.IsInstanceValid(entriesContainer))
            {
                return;
            }

            entriesContainer.AddChild(BuildRow(
                artwork,
                () => RebuildEntries(entriesContainer)));
            RefreshLayout(entriesContainer);
        }
    }

    private static Control BuildRow(FavoriteArtworkItem artwork, Action refreshEntries)
    {
        HBoxContainer row = new()
        {
            CustomMinimumSize = new Vector2(0f, 116f)
        };
        row.AddThemeConstantOverride("separation", 12);

        row.AddChild(new TextureRect
        {
            CustomMinimumSize = new Vector2(ThumbnailWidth, ThumbnailHeight),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = LoadThumbnail(artwork.PngBase64)
        });

        VBoxContainer info = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        info.AddThemeConstantOverride("separation", 6);
        row.AddChild(info);

        string targetVariable = artwork.Kind == FavoriteArtworkKind.Card ? "Card" : "Relic";
        Label artworkName = new()
        {
            Text = ModText.Format(
                artwork.Kind == FavoriteArtworkKind.Card
                    ? "DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.CARD"
                    : "DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.RELIC",
                (targetVariable, artwork.DisplayName)),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        artworkName.AddThemeFontSizeOverride("font_size", 20);
        info.AddChild(artworkName);

        Label state = new()
        {
            Text = ModText.Get(artwork.Enabled
                ? "DRAW_AND_GUESS_MOD.FAVORITE_ARTWORK_VIEWER.ACTIVE"
                : "DRAW_AND_GUESS_MOD.FAVORITE_ARTWORK_VIEWER.DISABLED_IN_CARD_LIBRARY"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        state.AddThemeFontSizeOverride("font_size", 14);
        state.AddThemeColorOverride("font_color", new Color("9AA3AD"));
        info.AddChild(state);

        HBoxContainer actions = new();
        actions.AddThemeConstantOverride("separation", 8);
        info.AddChild(actions);

        Button toggle = CreateActionButton(ModText.Get(artwork.Enabled
            ? "DRAW_AND_GUESS_MOD.FAVORITE_ARTWORK_VIEWER.CANCEL_PERMANENT_ARTWORK"
            : "DRAW_AND_GUESS_MOD.FAVORITE_ARTWORK_VIEWER.SET_AS_PERMANENT_ARTWORK"));
        toggle.Pressed += () =>
        {
            bool success = artwork.Kind == FavoriteArtworkKind.Card
                ? MemorialSketchbookStore.SetPermanentArtworkEnabled(
                    artwork.TargetId,
                    artwork.ArtworkId,
                    !artwork.Enabled)
                : MemorialSketchbookStore.SetPermanentRelicArtworkEnabled(
                    artwork.TargetId,
                    artwork.ArtworkId,
                    !artwork.Enabled);
            if (success)
            {
                RefreshVisibleArtworkNodes();
                refreshEntries();
            }
            else
            {
                toggle.TooltipText = ModText.Get(
                    "DRAW_AND_GUESS_MOD.FAVORITE_ARTWORK_VIEWER.FAILED_TO_TOGGLE_PERMANENT_ARTWORK");
            }
        };
        actions.AddChild(toggle);

        Button remove = CreateActionButton(
            ModText.Get("DRAW_AND_GUESS_MOD.FAVORITE_ARTWORK_VIEWER.REMOVE_FROM_COLLECTION"));
        remove.Pressed += () =>
        {
            bool success = artwork.Kind == FavoriteArtworkKind.Card
                ? MemorialSketchbookStore.ClearPermanentArtwork(
                    artwork.TargetId,
                    artwork.ArtworkId)
                : MemorialSketchbookStore.ClearPermanentRelicArtwork(
                    artwork.TargetId,
                    artwork.ArtworkId);
            if (success)
            {
                RefreshVisibleArtworkNodes();
                refreshEntries();
            }
            else
            {
                remove.TooltipText = ModText.Get(
                    "DRAW_AND_GUESS_MOD.FAVORITE_ARTWORK_VIEWER.FAILED_TO_REMOVE_FROM_COLLECTION");
            }
        };
        actions.AddChild(remove);

        return row;
    }

    private static Button CreateActionButton(string text)
    {
        return new Button
        {
            Text = text,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(150f, 32f)
        };
    }

    private static string ResolveCardDisplayName(string cardId)
    {
        try
        {
            CardModel? card = ModelDb.AllCards.FirstOrDefault(candidate =>
                string.Equals(candidate.Id.ToString(), cardId, StringComparison.Ordinal) ||
                string.Equals(candidate.Id.Entry, cardId, StringComparison.Ordinal));
            return card?.Title ?? cardId;
        }
        catch
        {
            return cardId;
        }
    }

    private static string ResolveRelicDisplayName(string relicId)
    {
        try
        {
            RelicModel? relic = ModelDb.AllRelics.FirstOrDefault(candidate =>
                string.Equals(candidate.Id.ToString(), relicId, StringComparison.Ordinal) ||
                string.Equals(candidate.Id.Entry, relicId, StringComparison.Ordinal));
            return relic?.Title.GetFormattedText() ?? relicId;
        }
        catch
        {
            return relicId;
        }
    }

    private static Texture2D? LoadThumbnail(string pngBase64)
    {
        try
        {
            Image image = new();
            byte[] png = Convert.FromBase64String(pngBase64);
            return image.LoadPngFromBuffer(png) == Error.Ok
                ? ImageTexture.CreateFromImage(image)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void RefreshVisibleArtworkNodes()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        foreach (Node node in EnumerateDescendants(tree.Root))
        {
            try
            {
                if (!node.IsNodeReady())
                {
                    continue;
                }

                if (node is NCard && CardReloadMethod != null)
                {
                    CardReloadMethod.Invoke(node, null);
                }
                else if (node is NRelic && RelicReloadMethod != null)
                {
                    RelicReloadMethod.Invoke(node, null);
                }
            }
            catch (Exception ex)
            {
                Entry.Logger.Warn($"[DrawAndGuessMod] Failed to refresh an artwork node: {ex.Message}");
            }
        }
    }

    private static IEnumerable<Node> EnumerateDescendants(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            yield return child;
            foreach (Node descendant in EnumerateDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RefreshLayout(VBoxContainer entriesContainer)
    {
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(entriesContainer))
            {
                return;
            }

            entriesContainer.UpdateMinimumSize();
            entriesContainer.QueueSort();
            Control? ancestor = entriesContainer.GetParent() as Control;
            while (ancestor != null)
            {
                ancestor.UpdateMinimumSize();
                if (ancestor is Container container)
                {
                    container.QueueSort();
                }

                if (ancestor is ScrollContainer)
                {
                    break;
                }

                ancestor = ancestor.GetParent() as Control;
            }
        }).CallDeferred();
    }

    private enum FavoriteArtworkKind
    {
        Card,
        Relic
    }

    private sealed record FavoriteArtworkItem(
        FavoriteArtworkKind Kind,
        string TargetId,
        string ArtworkId,
        string PngBase64,
        bool Enabled,
        string DisplayName);
}
