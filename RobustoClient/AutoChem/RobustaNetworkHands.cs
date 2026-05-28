using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Content.Shared.Interaction;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Chemistry; 

namespace RobustoClient.Systems.AutoChem;

public sealed class RobustaNetworkHands : EntitySystem
{
    [Dependency] private readonly SharedInteractionSystem _interact = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _bui = default!;

    public void InsertItem(EntityUid player, EntityUid item, EntityUid machine)
    {
        var clickPos = Transform(machine).Coordinates;
        _interact.InteractUsing(player, item, machine, clickPos);
    }

    public void EjectByClick(EntityUid player, EntityUid machine)
    {
        _interact.InteractHand(player, machine);
    }

    public void EjectViaUI(EntityUid machine)
    {
        var ejectMsg = new ItemSlotButtonPressedEvent("beakerSlot", tryEject: true, tryInsert: false);
        var altMsg = new ItemSlotButtonPressedEvent("beaker", tryEject: true, tryInsert: false);

        SendBuiMessage(machine, ejectMsg);
        SendBuiMessage(machine, altMsg);
    }

    public void MakePills(EntityUid chemMaster, uint dosage, uint count, string label)
    {
        // Removed ChemMasterMode. Pills are now created directly from the buffer!
        SendBuiMessage(chemMaster, new ChemMasterCreatePillsMessage(dosage, count, label));
    }

    public void SendBuiMessage(EntityUid machine, BoundUserInterfaceMessage message)
    {
        if (TryComp<UserInterfaceComponent>(machine, out var uiComp))
        {
            // Using ClientOpenInterfaces instead of the private Interfaces
            foreach (var key in uiComp.ClientOpenInterfaces.Keys)
            {
                _bui.ClientSendUiMessage((machine, uiComp), key, message);
            }
        }
    }
}