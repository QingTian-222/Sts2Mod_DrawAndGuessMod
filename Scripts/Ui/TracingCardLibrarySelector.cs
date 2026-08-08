using System;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;

namespace DrawAndGuessMod.Scripts.Ui;

/// <summary>
/// Hosts the vanilla card library as a temporary local-only picker. The library keeps its
/// native search, filters, sorting and virtualized grid; only card-click and back behavior
/// are replaced so the first click returns a tracing reference instead of opening details.
/// </summary>
internal static class TracingCardLibrarySelector
{
    public static async Task<CardModel?> SelectAsync(Control host, Player owner)
    {
        NCardLibrary? library = NCardLibrary.Create();
        if (library == null)
        {
            return null;
        }

        TaskCompletionSource<CardModel?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Control overlay = new()
        {
            Name = "DrawAndGuessMod_TracingCardLibrary",
            ZIndex = 1000,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        host.AddChild(overlay);

        ColorRect dimmer = new()
        {
            Name = "CardLibraryDimmer",
            Color = new Color(0f, 0f, 0f, 0.68f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        dimmer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        overlay.AddChild(dimmer);

        library.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        library.Initialize(owner.RunState);
        overlay.AddChild(library);
        overlay.TreeExiting += () => completion.TrySetResult(null);

        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            overlay.QueueFree();
            return null;
        }

        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        if (!GodotObject.IsInstanceValid(overlay) || !GodotObject.IsInstanceValid(library))
        {
            return null;
        }

        NCardLibraryGrid grid = library.GetNode<NCardLibraryGrid>("%CardGrid");
        NBackButton backButton = library.GetNode<NBackButton>("BackButton");
        DisconnectAll(grid, NCardGrid.SignalName.HolderPressed);
        DisconnectAll(grid, NCardGrid.SignalName.HolderAltPressed);
        DisconnectAll(backButton, NClickableControl.SignalName.Released);

        Callable chooseCard = Callable.From<NCardHolder>(holder =>
            completion.TrySetResult(holder.CardModel));
        grid.Connect(NCardGrid.SignalName.HolderPressed, chooseCard);
        grid.Connect(NCardGrid.SignalName.HolderAltPressed, chooseCard);
        backButton.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NButton>(_ => completion.TrySetResult(null)));
        backButton.Enable();

        try
        {
            library.OnSubmenuOpened();
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Failed to initialize the tracing card library filters: {ex.Message}");
            grid.FilterCards(card => card.ShouldShowInCardLibrary);
        }

        CardModel? selected;
        try
        {
            selected = await completion.Task;
        }
        finally
        {
            if (GodotObject.IsInstanceValid(library))
            {
                library.OnSubmenuClosed();
            }
            if (GodotObject.IsInstanceValid(overlay))
            {
                overlay.QueueFree();
            }
        }

        return selected;
    }

    private static void DisconnectAll(GodotObject source, StringName signal)
    {
        foreach (Godot.Collections.Dictionary connection in source.GetSignalConnectionList(signal))
        {
            Callable callable = connection["callable"].AsCallable();
            if (source.IsConnected(signal, callable))
            {
                source.Disconnect(signal, callable);
            }
        }
    }
}
