using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.RelicPools;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace DrawAndGuessMod.Scripts.Relics;

[RegisterRelic(typeof(EventRelicPool))]
public sealed class MemorialSketchbook : ModRelicTemplate
{
    private const string RelicIconPath = "res://images/memorial_sketchbook_relic.png";

    public override RelicRarity Rarity => RelicRarity.Event;

    public override RelicAssetProfile AssetProfile => new(
        RelicIconPath,
        RelicIconPath,
        RelicIconPath);
}
