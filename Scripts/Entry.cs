using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using DrawAndGuessMod.Scripts.Ai;
using DrawAndGuessMod.Scripts.Assets;
using DrawAndGuessMod.Scripts.Config;
using DrawAndGuessMod.Scripts.Guess;
using DrawAndGuessMod.Scripts.Localization;
using DrawAndGuessMod.Scripts.Networking;
using DrawAndGuessMod.Scripts.RestSite;
using DrawAndGuessMod.Scripts.State;
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
            ErasedCardStore.Register();
            GalleryChallengeStore.Register();
            CardLocalization.Install();
            EventLocalization.Install();
            RelicLocalization.Install();
            CardArtClassifier.Preload();

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

        foreach (string candidate in GetNativeCandidates(libraryName))
        {
            if (File.Exists(candidate))
            {
                return NativeLibrary.Load(candidate);
            }
        }

        return nint.Zero;
    }

    /// <summary>按平台拼出原生库候选文件名（Windows: onnxruntime.dll；macOS: libonnxruntime.dylib；Linux: libonnxruntime.so）。</summary>
    private static IEnumerable<string> GetNativeCandidates(string libraryName)
    {
        string baseName = libraryName;
        if (baseName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^4];
        }

        if (baseName.StartsWith("lib", StringComparison.OrdinalIgnoreCase) && !OperatingSystem.IsWindows())
        {
            baseName = baseName[3..];
        }

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(_assemblyDirectory!, baseName + ".dll");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(_assemblyDirectory!, "lib" + baseName + ".dylib");
            yield return Path.Combine(_assemblyDirectory!, baseName + ".dylib");
        }
        else
        {
            yield return Path.Combine(_assemblyDirectory!, "lib" + baseName + ".so");
            yield return Path.Combine(_assemblyDirectory!, baseName + ".so");
        }

        // 兜底：原始名称原样尝试（调用方可能已给出完整文件名）。
        yield return Path.Combine(_assemblyDirectory!, libraryName);
    }

    private static void OnRunStarted(RunState runState)
    {
        ArtworkStore.ActivateRun(runState);
        DeathNoteRestSiteOption.ResetSessions();
        DrawingNetSync.Reset();
        GuessPhaseCoordinator.Reset();
    }

}
