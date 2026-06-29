using System;
using System.Collections.Generic;
using System.Linq;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Content.Shared.Chemistry;
using Content.Shared.FixedPoint;
using RobustoClient.Systems.AutoChem;

namespace RobustoClient.Systems.AutoChem;

public sealed class AutoChemContext
{
    public readonly RobustaWorldScanner Scanner;
    public readonly RobustaNetworkHands NetworkHands;
    public readonly IEntityManager EntManager;
    public readonly ISawmill Logger;

    public DispensePlan? CurrentPlan { get; set; }
    public EntityUid? Dispenser { get; set; }
    public EntityUid? Heater { get; set; }
    public EntityUid? Beaker { get; set; }

    public float Timer { get; set; } = 0f;
    public float ExamineSyncTimer { get; set; } = 0f;
    public float StabilizationTimer { get; set; } = 0f;
    public float? LastNetworkTemp { get; set; }
    public bool TargetTempReached { get; set; } = false;
    public Queue<(string Reagent, int Amount)> DispenseQueue { get; set; } = new();

    public string? LastReagentAdded { get; set; }
    public string? TargetProduct { get; set; }
    public int LastRequestedDose { get; set; }
    public float LastReagentAmountBefore { get; set; }
    public float LastTotalAmountBefore { get; set; }
    public float LastProductAmountBefore { get; set; }
    public ReagentDispenserDispenseAmount? LastActiveDose { get; set; }
    public bool WaitingForDispenseConfirm { get; set; } = false;

    public AutoChemContext(
        RobustaWorldScanner scanner, 
        RobustaNetworkHands networkHands, 
        IEntityManager entManager, 
        ISawmill logger)
    {
        Scanner = scanner;
        NetworkHands = networkHands;
        EntManager = entManager;
        Logger = logger;
    }

    public void Reset()
    {
        Timer = 0f;
        ExamineSyncTimer = 0f;
        StabilizationTimer = 0f;
        LastNetworkTemp = null;
        TargetTempReached = false;
        DispenseQueue.Clear();
        WaitingForDispenseConfirm = false;
        LastActiveDose = null;
        LastTotalAmountBefore = 0f;
        LastProductAmountBefore = 0f;
        LastReagentAmountBefore = 0f;
        LastReagentAdded = null;
        TargetProduct = null;
        Dispenser = null;
        Heater = null;
    }

    public void LogBeakerContents()
    {
        if (Beaker == null) return;
        var contents = Scanner.GetFullBreakdown(Beaker.Value);
        if (contents.Count == 0)
        {
            Logger.Info("[CONTENT] Beaker is empty.");
            return;
        }
        var list = string.Join(", ", contents.Select(kv => $"{kv.Key}: {kv.Value:F1}u"));
        Logger.Info($"[CONTENT] In beaker: {list}");
    }
}
