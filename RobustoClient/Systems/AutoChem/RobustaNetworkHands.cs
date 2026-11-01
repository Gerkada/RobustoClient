using System;
using System.Reflection;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Content.Shared.Interaction;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Chemistry; 
using Content.Shared.Storage;

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
        _interact.GetType().GetMethod("InteractHand")?.Invoke(_interact, [player, machine]);
    }

    public void EjectViaUI(EntityUid machine)
    {
        SendBuiMessage(machine, new ItemSlotButtonPressedEvent("beakerSlot", tryEject: true, tryInsert: false));
        SendBuiMessage(machine, new ItemSlotButtonPressedEvent("beaker", tryEject: true, tryInsert: false));
        SendBuiMessage(machine, new ItemSlotButtonPressedEvent("energyBeakerSlot", tryEject: true, tryInsert: false));
    }

    public void SetDispenserDose(EntityUid dispenser, ReagentDispenserDispenseAmount dose)
    {
        if (TryCreateGoobEnergyDispenserMessage("Content.Goobstation.Shared.Chemistry.EnergyReagentDispenserSetDispenseAmountMessage", ((int)dose).ToString(), out var energyMsg) &&
            IsEnergyDispenserUiOpen(dispenser))
        {
            SendBuiMessage(dispenser, energyMsg!);
            return;
        }

        SendBuiMessage(dispenser, new ReagentDispenserSetDispenseAmountMessage(dose));
    }

    public void DispenseReagent(EntityUid dispenser, ItemStorageLocation? jugLoc, string? energyReagentId)
    {
        if (jugLoc != null)
        {
            SendBuiMessage(dispenser, new ReagentDispenserDispenseReagentMessage(jugLoc.Value));
            return;
        }

        if (energyReagentId != null &&
            TryCreateGoobEnergyDispenserMessage("Content.Goobstation.Shared.Chemistry.EnergyReagentDispenserDispenseReagentMessage", energyReagentId, out var energyMsg))
        {
            SendBuiMessage(dispenser, energyMsg!);
        }
    }

    private bool IsEnergyDispenserUiOpen(EntityUid dispenser)
    {
        if (!TryComp<UserInterfaceComponent>(dispenser, out var uiComp))
            return false;

        foreach (var ui in uiComp.ClientOpenInterfaces.Values)
        {
            if (ui.GetType().Name.Contains("EnergyReagentDispenser", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool TryCreateGoobEnergyDispenserMessage(string fullTypeName, string arg, out BoundUserInterfaceMessage? message)
    {
        message = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType(fullTypeName);
            if (type == null)
                continue;

            var ctor = type.GetConstructor([typeof(string)]);
            if (ctor?.Invoke([arg]) is BoundUserInterfaceMessage msg)
            {
                message = msg;
                return true;
            }
        }
        return false;
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