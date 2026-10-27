using Content.Shared.Doors.Components;
using Content.Shared.Tools.Systems;
using Content.Shared.Wires;

namespace RobustoClient.Systems.Hacking;

public sealed partial class RobustaWiresSolver
{
    private void RegisterDefaultIntents()
    {
        RegisterIntent(new WiresIntentDefinition("DisablePower", [
            new EnsurePanelOpenStep(),
            new FindWireStep(PowerWireName, "Power", true),
            new PerformWireActionStep("Power", WiresAction.Cut, SharedToolSystem.CutQuality),
            new VerifyWireStateStep("Power", true)]));

        RegisterIntent(new WiresIntentDefinition("PryingOpen", [
            new EnsurePanelOpenStep(),
            new UseIntentIfBoltedStep("Unbolt"),
            new FindWireStep(LogWireName, "Log", Optional: true),
            new PerformWireActionStep("Log", WiresAction.Cut, SharedToolSystem.CutQuality),
            new VerifyWireStateStep("Log", true),
            new FindWireStep(AiInteractWireName, "AiInteract", Optional: true),
            new PerformWireActionStep("AiInteract", WiresAction.Cut, SharedToolSystem.CutQuality),
            new VerifyWireStateStep("AiInteract", true),
            new UseWiresIntentStep("DisablePower"),
            new UseToolOnTargetStep("Prying", DoorState.Open)]));

        RegisterIntent(new WiresIntentDefinition("VendingAccess", [
            new EnsurePanelOpenStep(),
            new FindWireStep(AccessWireName, "Access"),
            new PerformWireActionStep("Access", WiresAction.Cut, SharedToolSystem.CutQuality),
            new VerifyWireStateStep("Access", true),
            new FindWireStep(VendingManagerWireName, "Manager"),
            new PerformWireActionStep("Manager", WiresAction.Cut, SharedToolSystem.CutQuality),
            new VerifyWireStateStep("Manager", true),
            new FindWireStep(LogWireName, "Log"),
            new PerformWireActionStep("Log", WiresAction.Cut, SharedToolSystem.CutQuality),
            new VerifyWireStateStep("Log", true)]));

        RegisterIntent(new WiresIntentDefinition("Bolt", [
            new EnsurePanelOpenStep(),
            new FindWireStep(BoltWireName, "Bolt"),
            new PerformWireActionStep("Bolt", WiresAction.Cut, SharedToolSystem.CutQuality),
            new VerifyWireStateStep("Bolt", true)], AllowNormallyAvoidedWires: true));

        RegisterIntent(new WiresIntentDefinition("Unbolt", [
            new EnsurePanelOpenStep(),
            new FindWireStep(BoltWireName, "Bolt"),
            new PerformWireActionStep("Bolt", WiresAction.Mend, SharedToolSystem.CutQuality),
            new VerifyWireStateStep("Bolt", false),
            new PerformWireActionStep("Bolt", WiresAction.Pulse, SharedToolSystem.PulseQuality)],
            AllowNormallyAvoidedWires: true));
    }
}