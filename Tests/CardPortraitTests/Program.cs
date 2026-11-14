using System.Reflection;
using DrawAndGuessMod.Scripts.Cards;
using DrawAndGuessMod.Scripts.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

int passed = 0;
Harmony harmony = new("drawandguess.tests.portrait");
MethodInfo portraitPostfix = AccessTools.Method(typeof(NCardPortraitPatch), "Postfix");

// Reproduce the old coverage gap before registering the new dual-target patch.
harmony.CreateClassProcessor(typeof(CardPortraitPatch)).Patch();
harmony.Patch(AccessTools.Method(typeof(NCard), "Reload"), postfix: new HarmonyMethod(portraitPostfix));
NCard regressionCard = new() { Model = new Blank(), BypassGetter = true };
regressionCard.Reload();
Assert(!ReferenceEquals(regressionCard.Nodes["%Portrait"].Texture, regressionCard.Model.VanillaPortrait), "old Reload fallback applies");
regressionCard.UpdateVisuals();
Assert(ReferenceEquals(regressionCard.Nodes["%Portrait"].Texture, regressionCard.Model.VanillaPortrait), "old Reload-only fallback loses artwork on later refresh");
harmony.Unpatch(AccessTools.Method(typeof(NCard), "Reload"), HarmonyPatchType.Postfix, harmony.Id);
harmony.CreateClassProcessor(typeof(NCardPortraitPatch)).Patch();
harmony.CreateClassProcessor(typeof(NCardInspectPortraitPatch)).Patch();

foreach (string method in new[] { "Reload", "UpdatePortrait" })
    Assert(Harmony.GetPatchInfo(AccessTools.Method(typeof(NCard), method))!.Postfixes.Any(p => p.owner == harmony.Id), $"real Harmony registered {method}");

foreach (bool bypassGetter in new[] { false, true })
{
    string mode = bypassGetter ? "getter bypassed" : "getter called";
    NCard card = new() { Model = new Blank(), BypassGetter = bypassGetter };
    CardPortraitPatch.TryGetOverrideTexture(card.Model, out Texture2D paper);
    card.Reload();
    Assert(ReferenceEquals(card.Nodes["%Portrait"].Texture, paper), $"Blank Reload: {mode}");
    card.UpdateVisuals();
    Assert(ReferenceEquals(card.Nodes["%Portrait"].Texture, paper), $"Blank later refresh: {mode}");

    CardModel drawn = new() { Artwork = new Texture2D() };
    card.Model = drawn;
    card.Reload();
    card.UpdateVisuals();
    Assert(ReferenceEquals(card.Nodes["%Portrait"].Texture, drawn.Artwork), $"drawn artwork survives refresh: {mode}");
    card.SetPortraitTexture(new Texture2D());
    Assert(ReferenceEquals(card.Nodes["%Portrait"].Texture, drawn.Artwork), $"drawn artwork survives inspect's full-size portrait: {mode}");
    int assignments = card.Nodes["%Portrait"].Assignments;
    portraitPostfix.Invoke(null, [card]);
    Assert(card.Nodes["%Portrait"].Assignments == assignments, $"unchanged override is idempotent: {mode}");

    card.Model = new CardModel();
    card.Reload();
    card.UpdateVisuals();
    Assert(ReferenceEquals(card.Nodes["%Portrait"].Texture, card.Model.VanillaPortrait), $"pooled holder does not keep previous artwork: {mode}");
    Texture2D fullSizePortrait = new();
    card.SetPortraitTexture(fullSizePortrait);
    Assert(ReferenceEquals(card.Nodes["%Portrait"].Texture, fullSizePortrait), $"inspect keeps vanilla full-size portrait without override: {mode}");

    card.Model = new CardModel { Rarity = CardRarity.Ancient, Artwork = new Texture2D() };
    Texture2D? standardBefore = card.Nodes["%Portrait"].Texture;
    card.Reload();
    card.UpdateVisuals();
    Assert(ReferenceEquals(card.Nodes["%AncientPortrait"].Texture, card.Model.Artwork), $"ancient portrait node: {mode}");
    Assert(ReferenceEquals(card.Nodes["%Portrait"].Texture, standardBefore), $"ancient override leaves standard node alone: {mode}");

    card.Model = new CardModel { Artwork = new Texture2D(), HasPermanentArtwork = true };
    card.Reload();
    card.UpdateVisuals();
    Assert(ReferenceEquals(card.Nodes["%Portrait"].Texture, card.Model.VanillaPortrait), $"disabled permanent drawing retains vanilla art: {mode}");
    card.Model.PermanentArtwork = new Texture2D();
    card.UpdateVisuals();
    Assert(ReferenceEquals(card.Nodes["%Portrait"].Texture, card.Model.PermanentArtwork), $"permanent artwork keeps priority: {mode}");
    card.Model.PreviewArtwork = new Texture2D();
    card.UpdateVisuals();
    Assert(ReferenceEquals(card.Nodes["%Portrait"].Texture, card.Model.PreviewArtwork), $"collection preview keeps priority: {mode}");
}

NCard notReady = new() { Ready = false, Model = new Blank(), BypassGetter = true };
notReady.Reload();
Assert(notReady.Nodes["%Portrait"].Assignments == 0, "not-ready nodes are untouched");
NCard withoutModel = new();
withoutModel.Reload();
withoutModel.UpdateVisuals();
Assert(withoutModel.Nodes["%Portrait"].Assignments == 0, "missing model is safe");
NCard withoutNode = new() { Model = new Blank() };
withoutNode.Nodes.Clear();
withoutNode.Reload();
withoutNode.UpdateVisuals();
Assert(withoutNode.Nodes.Count == 0, "missing portrait node is safe");
Console.WriteLine($"Passed {passed} portrait regression checks (UI/storage doubles, real Harmony; not an in-game RitsuLib test).");

void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
    Console.WriteLine($"PASS: {message}");
}
