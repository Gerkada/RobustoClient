using RobustoClient.Systems.Abstract;
using Content.Shared.Overlays;

namespace RobustoClient.Systems;

public class RobustaHealthBarSystem : LocalPlayerAddCompSystem<ShowHealthBarsComponent>
{
    protected override ShowHealthBarsComponent CompOverride =>
        new ShowHealthBarsComponent { DamageContainers = ["Biological", "Silicon"] };
}