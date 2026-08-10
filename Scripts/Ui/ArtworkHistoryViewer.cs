using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.State;
using Godot;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Settings;

namespace DrawAndGuessMod.Scripts.Ui;

/// <summary>
/// Gallery content embedded in the RitsuLib settings page. The framework owns
/// the page navigation, background, typography, and scrolling surface.
/// </summary>
internal static class ArtworkHistoryViewer
{
    private const int ThumbnailWidth = 130;
    private const int ThumbnailHeight = 100;
    private const string ArtworkHistoryPageId = "artwork_history";
    private static int _rebuildGeneration;

    public static Control BuildSettingsControl(IModSettingsUiActionHost host)
    {
        VBoxContainer root = new()
        {
            Name = "DrawAndGuessMod_ArtworkHistory",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        root.AddThemeConstantOverride("separation", 10);

        root.AddChild(CreateOpenFolderButton());

        VBoxContainer entriesContainer = new()
        {
            Name = "DrawAndGuessMod_ArtworkHistoryEntries",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        root.AddChild(entriesContainer);

        root.TreeEntered += () => RebuildEntries(entriesContainer, host);

        root.VisibilityChanged += () =>
        {
            if (root.IsVisibleInTree())
            {
                RebuildEntries(entriesContainer, host);
            }
        };

        return root;
    }

    private static Button CreateOpenFolderButton()
    {
        Button button = new()
        {
            Text = ModText.Get("DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.OPEN_ARTWORK_FOLDER"),
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, 34f),
            TooltipText = ModText.Get("DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.OPEN_THE_FOLDER_THAT_STORES_THE_ARTWORK")
        };
        button.Pressed += () => OS.ShellShowInFileManager(DrawingHistoryStore.EnsureHistoryDirectory());
        return button;
    }

    private static void RebuildEntries(VBoxContainer entriesContainer, IModSettingsUiActionHost host)
    {
        int generation = ++_rebuildGeneration;
        foreach (Node child in entriesContainer.GetChildren())
        {
            entriesContainer.RemoveChild(child);
            child.QueueFree();
        }

        entriesContainer.AddThemeConstantOverride("separation", 10);
        List<DrawingHistoryEntry> entries = DrawingHistoryStore.GetEntries()
            .OrderByDescending(entry => entry.Timestamp)
            .ToList();
        if (entries.Count == 0)
        {
            Label empty = new()
            {
                Text = ModText.Get("DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.NO_ARTWORK_HISTORY_YET_FINISH_A_BLANK"),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            entriesContainer.AddChild(empty);
        }
        else
        {
            _ = AddRowsAsync(entriesContainer, host, entries, generation);
        }

        RefreshLayout(entriesContainer);
    }

    private static async Task AddRowsAsync(
        VBoxContainer entriesContainer,
        IModSettingsUiActionHost host,
        IReadOnlyList<DrawingHistoryEntry> entries,
        int generation)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        foreach (DrawingHistoryEntry entry in entries)
        {
            // PNG decoding and model-name resolution can be expensive for a long
            // history. Spread one row across each frame instead of stalling the
            // settings page while every thumbnail is decoded at once.
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (generation != _rebuildGeneration || !GodotObject.IsInstanceValid(entriesContainer))
            {
                return;
            }

            entriesContainer.AddChild(BuildRow(
                entry,
                host,
                () => RebuildEntries(entriesContainer, host)));
            RefreshLayout(entriesContainer);
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

    private static Control BuildRow(
        DrawingHistoryEntry entry,
        IModSettingsUiActionHost host,
        Action refreshEntries)
    {
        HBoxContainer row = new();
        row.AddThemeConstantOverride("separation", 12);
        row.CustomMinimumSize = new Vector2(0f, 142f);

        TextureRect thumbnail = new()
        {
            CustomMinimumSize = new Vector2(ThumbnailWidth, ThumbnailHeight),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = LoadThumbnail(entry)
        };
        row.AddChild(thumbnail);

        VBoxContainer info = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        info.AddThemeConstantOverride("separation", 4);
        row.AddChild(info);

        LineEdit nameInput = new()
        {
            Text = entry.Name,
            PlaceholderText = ModText.Get("DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.ARTWORK_NAME"),
            TooltipText = ModText.Get("DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.EDIT_THE_NAME_THEN_SELECT_RENAME_TO"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, 30f)
        };
        nameInput.AddThemeFontSizeOverride("font_size", 18);
        info.AddChild(nameInput);

        Label targetLabel = new()
        {
            Text = ResolveTargetDisplayName(entry),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        targetLabel.AddThemeFontSizeOverride("font_size", 16);
        info.AddChild(targetLabel);

        string time = DateTimeOffset
            .FromUnixTimeMilliseconds(entry.Timestamp)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm");
        Label meta = new()
        {
            Text = time,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        meta.AddThemeFontSizeOverride("font_size", 13);
        meta.AddThemeColorOverride("font_color", new Color("9AA3AD"));
        info.AddChild(meta);

        HBoxContainer actions = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        actions.AddThemeConstantOverride("separation", 6);
        info.AddChild(actions);

        Button rename = CreateActionButton(ModText.Get("DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.RENAME"));
        rename.Pressed += () =>
        {
            if (DrawingHistoryStore.TryRename(
                    entry.Key,
                    nameInput.Text,
                    out string savedName,
                    out string savedFileName))
            {
                entry.Name = savedName;
                entry.FileName = savedFileName;
                nameInput.Text = savedName;
                refreshEntries();
            }
            else
            {
                rename.TooltipText = ModText.Get("DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.THE_NAME_CANNOT_BE_EMPTY_OR_SAVING");
            }
        };
        actions.AddChild(rename);

        Button edit = CreateActionButton(ModText.Get("DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.EDIT"));
        edit.Pressed += () => Callable.From(() =>
        {
            _ = EditAsync(entry, refreshEntries);
        }).CallDeferred();
        actions.AddChild(edit);

        Button copy = CreateActionButton(ModText.Get("DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.COPY"));
        copy.Pressed += () =>
        {
            byte[]? png = DrawingHistoryStore.LoadPng(entry.FileName);
            string error = png == null
                ? ModText.Get("DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.PNG_FILE_WAS_NOT_FOUND")
                : string.Empty;
            if (png != null && ArtworkClipboard.TryCopyPng(png, out error))
            {
                copy.Text = ModText.Get("DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.COPIED");
            }
            else
            {
                copy.TooltipText = ModText.Format(
                    "DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.FAILED_TO_COPY_IMAGE",
                    ("Error", error));
            }
        };
        actions.AddChild(copy);

        Button delete = CreateActionButton(ModText.Get("DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.DELETE"));
        delete.Pressed += () =>
        {
            if (DrawingHistoryStore.TryDelete(entry.Key))
            {
                refreshEntries();
            }
            else
            {
                delete.TooltipText = ModText.Get("DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.FAILED_TO_DELETE_THE_ARTWORK");
            }
        };
        actions.AddChild(delete);

        return row;
    }

    private static Button CreateActionButton(string text)
    {
        return new Button
        {
            Text = text,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(76f, 30f)
        };
    }

    private static async Task EditAsync(
        DrawingHistoryEntry entry,
        Action refreshEntries)
    {
        byte[]? png = DrawingHistoryStore.LoadPng(entry.FileName);
        if (png == null)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Cannot edit missing history PNG '{entry.FileName}'.");
            return;
        }

        try
        {
            // The settings submenu stays open underneath the full-screen editor
            // (DrawingScreen renders an opaque backdrop above it), so closing the
            // editor naturally lands back on the artwork history page. No submenu
            // pop/push round-trip is needed here.
            await DrawingScreen.ShowHistoryEditAsync(png, entry.Name, edited =>
            {
                try
                {
                    if (edited != null && !DrawingHistoryStore.TryReplacePng(entry.Key, edited))
                    {
                        Entry.Logger.Warn($"[DrawAndGuessMod] Failed to save edited history drawing '{entry.Key}'.");
                    }
                }
                finally
                {
                    refreshEntries();
                }
            });
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to edit history drawing '{entry.Key}': {ex}");
        }
    }

    private static string ResolveTargetDisplayName(DrawingHistoryEntry entry)
    {
        try
        {
            if (string.Equals(entry.Kind, "relic", StringComparison.Ordinal))
            {
                RelicModel? relic = ModelDb.AllRelics.FirstOrDefault(
                    relic => string.Equals(relic.Id.Entry, entry.TargetId, StringComparison.Ordinal));
                string relicName = relic == null ? entry.TargetId : relic.Title.GetFormattedText();
                return string.IsNullOrWhiteSpace(entry.WorkTitle)
                    ? ModText.Format(
                        "DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.RELIC",
                        ("Relic", relicName))
                    : ModText.Format(
                        "DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.RELIC_WITH_WORK_TITLE",
                        ("Relic", relicName),
                        ("WorkTitle", entry.WorkTitle));
            }

            CardModel? card = ModelDb.AllCards.FirstOrDefault(
                card => string.Equals(card.Id.Entry, entry.TargetId, StringComparison.Ordinal));
            string cardName = card == null ? entry.TargetId : card.Title;
            return ModText.Format(
                "DRAW_AND_GUESS_MOD.ARTWORK_HISTORY_VIEWER.CARD",
                ("Card", cardName));
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to resolve history entry display name: {ex.Message}");
            return entry.TargetId;
        }
    }

    private static Texture2D? LoadThumbnail(DrawingHistoryEntry entry)
    {
        byte[]? png = DrawingHistoryStore.LoadPng(entry.FileName);
        if (png == null)
        {
            return null;
        }

        try
        {
            Image image = new();
            return image.LoadPngFromBuffer(png) == Error.Ok
                ? ImageTexture.CreateFromImage(image)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
