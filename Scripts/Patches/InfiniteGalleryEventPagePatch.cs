using DrawAndGuessMod.Scripts.Events;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace DrawAndGuessMod.Scripts.Patches;

/// <summary>
/// Vanilla serializes shared-event page indexes with four bits. Keep the
/// infinitely repeatable gallery inside that wire range without changing the
/// global multiplayer packet format.
/// </summary>
[HarmonyPatch(typeof(EventSynchronizer), "ChooseOptionForSharedEvent")]
internal static class InfiniteGalleryEventPagePatch
{
    private const uint SerializedPageMask = 0xFu;

    [HarmonyPostfix]
    private static void KeepGalleryPageIndexSerializable(EventSynchronizer __instance)
    {
        Traverse synchronizer = Traverse.Create(__instance);
        EventModel? canonicalEvent = synchronizer
            .Field("_canonicalEvent")
            .GetValue<EventModel>();
        if (canonicalEvent is not VakuusInfiniteGallery)
        {
            return;
        }

        Traverse pageIndexField = synchronizer.Field("_pageIndex");
        uint pageIndex = pageIndexField.GetValue<uint>();
        uint serializedPageIndex = pageIndex & SerializedPageMask;
        if (pageIndex == serializedPageIndex)
        {
            return;
        }

        pageIndexField.SetValue(serializedPageIndex);
        Entry.Logger.Debug(
            $"[DrawAndGuessMod] Wrapped infinite gallery shared-event page " +
            $"{pageIndex} to {serializedPageIndex} for 4-bit network serialization.");
    }
}
