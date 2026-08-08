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
        RebuildEntries(entriesContainer, host);

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
            Text = ModText.Get("打开画作保存路径", "Open Artwork Folder"),
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, 34f),
            TooltipText = ModText.Get(
                "在文件管理器中打开保存画作 PNG 与索引的目录。",
                "Open the folder that stores the artwork PNGs and index in the file manager.")
        };
        button.Pressed += () => OS.ShellShowInFileManager(DrawingHistoryStore.EnsureHistoryDirectory());
        return button;
    }

    private static void RebuildEntries(VBoxContainer entriesContainer, IModSettingsUiActionHost host)
    {
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
                Text = ModText.Get(
                    "还没有历史画作。完成一次「空白」绘画、画廊挑战或遗物鉴定后，这里会显示对应的卡图。",
                    "No artwork history yet. Finish a Blank drawing, a gallery challenge, or a relic appraisal and it will appear here."),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            entriesContainer.AddChild(empty);
        }
        else
        {
            int generation = ++_rebuildGeneration;
            _ = AddRowsAsync(entriesContainer, host, entries, generation);
        }
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
        }
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
            PlaceholderText = ModText.Get("作品名称", "Artwork name"),
            TooltipText = ModText.Get("修改名称后点击“重命名”保存。", "Edit the name, then select Rename to save it."),
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

        Button rename = CreateActionButton(ModText.Get("重命名", "Rename"));
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
                rename.TooltipText = ModText.Get("名称不能为空，或保存失败。", "The name cannot be empty, or saving failed.");
            }
        };
        actions.AddChild(rename);

        Button edit = CreateActionButton(ModText.Get("编辑", "Edit"));
        edit.Pressed += () => Callable.From(() =>
        {
            _ = EditAsync(entry, refreshEntries);
        }).CallDeferred();
        actions.AddChild(edit);

        Button copy = CreateActionButton(ModText.Get("复制", "Copy"));
        copy.Pressed += () =>
        {
            byte[]? png = DrawingHistoryStore.LoadPng(entry.FileName);
            string error = png == null
                ? ModText.Get("找不到 PNG 文件。", "PNG file was not found.")
                : string.Empty;
            if (png != null && ArtworkClipboard.TryCopyPng(png, out error))
            {
                copy.Text = ModText.Get("已复制", "Copied");
            }
            else
            {
                copy.TooltipText = ModText.Get(
                    "复制图片失败：" + error,
                    "Failed to copy image: " + error);
            }
        };
        actions.AddChild(copy);

        Button delete = CreateActionButton(ModText.Get("删除", "Delete"));
        delete.Pressed += () =>
        {
            if (DrawingHistoryStore.TryDelete(entry.Key))
            {
                refreshEntries();
            }
            else
            {
                delete.TooltipText = ModText.Get("删除画作失败。", "Failed to delete the artwork.");
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
                    ? ModText.Get($"遗物：{relicName}", $"Relic: {relicName}")
                    : ModText.Get(
                        $"遗物：{relicName}（作品名：{entry.WorkTitle}）",
                        $"Relic: {relicName} (Work: {entry.WorkTitle})");
            }

            CardModel? card = ModelDb.AllCards.FirstOrDefault(
                card => string.Equals(card.Id.Entry, entry.TargetId, StringComparison.Ordinal));
            string cardName = card == null ? entry.TargetId : card.Title;
            return ModText.Get($"卡牌：{cardName}", $"Card: {cardName}");
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
