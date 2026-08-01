using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;

namespace DrawAndGuessMod.Scripts.Patches;

internal static class MemorialRunHistoryContext
{
    private static WeakReference<NRunHistory>? _screen;
    private static string _seed = string.Empty;
    private static long _startTime;

    public static void Set(NRunHistory screen, RunHistory history)
    {
        _screen = new WeakReference<NRunHistory>(screen);
        _seed = history.Seed;
        _startTime = history.StartTime;
    }

    public static void Clear(NRunHistory screen)
    {
        if (_screen?.TryGetTarget(out NRunHistory? current) == true &&
            ReferenceEquals(current, screen))
        {
            _screen = null;
            _seed = string.Empty;
            _startTime = 0;
        }
    }

    public static bool TryGet(out string seed, out long startTime)
    {
        seed = string.Empty;
        startTime = 0;
        if (_screen?.TryGetTarget(out NRunHistory? screen) != true ||
            !GodotObject.IsInstanceValid(screen) ||
            !screen.IsVisibleInTree())
        {
            return false;
        }

        seed = _seed;
        startTime = _startTime;
        return true;
    }
}

[HarmonyPatch(typeof(NRunHistory), "DisplayRun")]
internal static class RunHistoryDisplayMemorialPatch
{
    [HarmonyPostfix]
    private static void Postfix(NRunHistory __instance, RunHistory history)
    {
        MemorialRunHistoryContext.Set(__instance, history);
    }
}

[HarmonyPatch(typeof(NRunHistory), "OnSubmenuHidden")]
internal static class RunHistoryHideMemorialPatch
{
    [HarmonyPostfix]
    private static void Postfix(NRunHistory __instance)
    {
        MemorialRunHistoryContext.Clear(__instance);
    }
}
