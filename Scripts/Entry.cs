using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using DrawAndGuessMod.Scripts.Ai;
using DrawAndGuessMod.Scripts.Assets;
using DrawAndGuessMod.Scripts.Config;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.Networking;
using DrawAndGuessMod.Scripts.RestSite;
using DrawAndGuessMod.Scripts.State;
using DrawAndGuessMod.Scripts.Ui;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Interop;
using StsLogger = MegaCrit.Sts2.Core.Logging.Logger;

namespace DrawAndGuessMod.Scripts;

[ModInitializer(nameof(Init))]
public static class Entry
{
    public const string ModId = "DrawAndGuessMod";
    public static readonly StsLogger Logger = RitsuLibFramework.CreateLogger(ModId);

    private static Harmony? _harmony;
    private static AssemblyLoadContext? _loadContext;
    private static string? _assemblyDirectory;

    public static void Init()
    {
        try
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            InstallDependencyResolver(assembly);
            RitsuLibFramework.EnsureGodotScriptsRegistered(assembly, Logger);
            ModTypeDiscoveryHub.RegisterModAssembly(ModId, assembly);

            DrawAndGuessAssets.Install();
            DrawAndGuessSettings.Register();
            ArtworkStore.Register();
            DrawingPaletteStore.Register();
            BlankSelectionStore.Register();
            ErasedCardStore.Register();
            GalleryChallengeStore.Register();
            CardLocalization.Install();
            EventLocalization.Install();
            RelicLocalization.Install();
            CardArtClassifier.Preload();
            RelicArtClassifier.Preload();

            _harmony = new Harmony("sts2.qingtian.drawandguessmod");
            _harmony.PatchAll(assembly);

            RunManager.Instance.RunStarted -= OnRunStarted;
            RunManager.Instance.RunStarted += OnRunStarted;
            Logger.Info("[DrawAndGuessMod] Initialized.");
        }
        catch (Exception ex)
        {
            Logger.Error($"[DrawAndGuessMod] Init failed: {ex}");
            GD.PushError($"[DrawAndGuessMod] Init failed: {ex}");
        }
    }

    private static void InstallDependencyResolver(Assembly assembly)
    {
        if (_loadContext != null)
        {
            return;
        }

        _assemblyDirectory = Path.GetDirectoryName(assembly.Location);
        _loadContext = AssemblyLoadContext.GetLoadContext(assembly) ?? AssemblyLoadContext.Default;
        _loadContext.Resolving += ResolveManagedDependency;
        _loadContext.ResolvingUnmanagedDll += ResolveNativeDependency;
    }

    private static Assembly? ResolveManagedDependency(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (string.IsNullOrWhiteSpace(_assemblyDirectory) || string.IsNullOrWhiteSpace(assemblyName.Name))
        {
            return null;
        }

        string candidate = Path.Combine(_assemblyDirectory, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
    }

    private static nint ResolveNativeDependency(Assembly assembly, string libraryName)
    {
        if (string.IsNullOrWhiteSpace(_assemblyDirectory))
        {
            return nint.Zero;
        }

        string fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? libraryName
            : libraryName + ".dll";
        string candidate = Path.Combine(_assemblyDirectory, fileName);
        return File.Exists(candidate) ? NativeLibrary.Load(candidate) : nint.Zero;
    }

    private static void OnRunStarted(RunState runState)
    {
        ArtworkStore.ActivateRun(runState);
        RelicAppraisalFairArtworkStore.Reset();
        RelicAppraisalFairTreasureFlow.Reset();
        DeathNoteRestSiteOption.ResetSessions();
        DrawingNetSync.Reset();
    }
}
