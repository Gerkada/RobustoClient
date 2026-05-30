using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Content.Shared.Chemistry;
using Content.Shared.FixedPoint;

namespace RobustoClient.Systems.AutoChem;

public sealed class RobustaAutoChemSystem : EntitySystem
{
    [Dependency] private readonly RobustaWorldScanner _scanner = default!;
    [Dependency] private readonly RobustaNetworkHands _networkHands = default!;

    private ISawmill _sawmill = default!;
    
    // Properties for State Pattern access
    public IEntityManager EntManager => EntityManager;
    public RobustaWorldScanner Scanner => _scanner;
    public RobustaNetworkHands NetworkHands => _networkHands;
    public ISawmill Logger => _sawmill;
    public IChemState CurrentState { get; private set; } = new IdleState();

    public DispensePlan? CurrentPlan { get; set; }
    public EntityUid? Dispenser { get; set; }
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

    public static readonly Regex TempRegex = new(@"(\d+(\.\d+)?)\s*K", RegexOptions.Compiled);

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = Robust.Shared.Log.Logger.GetSawmill("autochem");
    }

    public void StartJob(string reagent, FixedPoint2 amount)
    {
        Beaker = _scanner.GetBeakerInHand();
        float capacity = 50f;
        if (Beaker != null) capacity = _scanner.GetBeakerCapacity(Beaker.Value);

        CurrentPlan = RobustaRecipeSolver.CreatePlan(reagent, amount, capacity);
        
        if (CurrentPlan == null) 
        {
            _sawmill.Error($"[DEBUG] ERROR: Recipe for {reagent} not found!");
            return; 
        }

        _sawmill.Info(CurrentPlan.GetPlanSummary());

        CurrentState = new CheckNextPhaseState();
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
        _sawmill.Info("[DEBUG] Auto-chemist ready.");
    }

    public void StopJob()
    {
        CurrentState = new IdleState();
        CurrentPlan = null;
        Dispenser = null;
        Beaker = null;
        _sawmill.Info("[AutoChem] Operation stopped.");
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (CurrentState is IdleState || CurrentPlan == null) return;

        Timer += frameTime;
        ExamineSyncTimer += frameTime;

        if (Timer < 0.5f) return;
        Timer = 0f;

        if (Beaker == null || !EntityManager.EntityExists(Beaker.Value))
        {
            Beaker = _scanner.GetBeakerInHand();
            if (Beaker == null) return;
            _sawmill.Info($"[DEBUG] Beaker detected.");
            LogBeakerContents();
        }

        CurrentState = CurrentState.Execute(frameTime, this);
    }

    public void LogBeakerContents()
    {
        if (Beaker == null) return;
        var contents = _scanner.GetFullBreakdown(Beaker.Value);
        if (contents.Count == 0)
        {
            _sawmill.Info("[CONTENT] Beaker is empty.");
            return;
        }
        var list = string.Join(", ", contents.Select(kv => $"{kv.Key}: {kv.Value:F1}u"));
        _sawmill.Info($"[CONTENT] In beaker: {list}");
    }

    public string GetStatusInfo()
    {
        return CurrentState.GetStatusInfo(this);
    }

    public float GetProgress()
    {
        return CurrentState.GetProgress(this);
    }
}

// ==========================================
// STATE IMPLEMENTATIONS
// ==========================================

public sealed class IdleState : IChemState
{
    public IChemState Execute(float frameTime, RobustaAutoChemSystem system) => this;
    public string GetStatusInfo(RobustaAutoChemSystem system) => "Idle";
    public float GetProgress(RobustaAutoChemSystem system) => 0f;
}

public sealed class CheckNextPhaseState : IChemState
{
    public IChemState Execute(float frameTime, RobustaAutoChemSystem system)
    {
        if (system.Beaker == null || system.CurrentPlan == null) return this;

        if (system.CurrentPlan.PhaseQueue.Count == 0)
        {
            system.Logger.Info("[DEBUG] Plan completed.");
            return new FinishState();
        }

        var nextPhase = system.CurrentPlan.PhaseQueue.Peek();
        var currentContents = system.Scanner.GetFullBreakdown(system.Beaker.Value);
        var currentVol = currentContents.Values.Sum();
        var capacity = system.Scanner.GetBeakerCapacity(system.Beaker.Value);

        if (currentVol > capacity + 0.5f)
        {
            system.Logger.Error($"[OVERFLOW] Volume {currentVol:F1}u exceeds beaker limit {capacity}u! STOPPING.");
            system.StopJob();
            return new IdleState();
        }

        if (Math.Abs(currentVol - system.LastTotalAmountBefore) > 0.1f)
        {
            system.StabilizationTimer += 0.5f;
            if (system.StabilizationTimer < 2.0f) 
            {
                system.Logger.Info($"[WAIT] Stabilizing solution... ({system.LastTotalAmountBefore:F1}u -> {currentVol:F1}u)");
                system.LastTotalAmountBefore = currentVol;
                return this; 
            }
        }

        system.StabilizationTimer = 0f;
        system.TargetTempReached = false;
        system.LastTotalAmountBefore = currentVol;

        // SMART SKIP: Only for wait and heat phases
        if (nextPhase.Type != PhaseType.Dispense)
        {
            // If product of this phase is already in the beaker
            if (nextPhase.TargetProduct != null && currentContents.ContainsKey(nextPhase.TargetProduct) && currentContents[nextPhase.TargetProduct] > 0.1f)
            {
                system.Logger.Info($"[SKIP] Phase '{nextPhase.Description}' skipped because {nextPhase.TargetProduct} is already obtained.");
                system.CurrentPlan.PhaseQueue.Dequeue();
                return this; 
            }

            // Check for products from FUTURE phases (global skip for Wait/Heat)
            foreach (var futurePhase in system.CurrentPlan.PhaseQueue.Skip(1))
            {
                if (futurePhase.TargetProduct != null && currentContents.ContainsKey(futurePhase.TargetProduct) && currentContents[futurePhase.TargetProduct] > 0.1f)
                {
                    system.Logger.Info($"[SKIP] Phase '{nextPhase.Description}' skipped because a product of a future phase was detected: {futurePhase.TargetProduct}");
                    system.CurrentPlan.PhaseQueue.Dequeue();
                    return this;
                }
            }
        }

        if (nextPhase.Type == PhaseType.Dispense)
        {
            if (system.DispenseQueue.Count == 0)
            {
                float totalToDispense = nextPhase.Ingredients.Values.Sum(v => v.Float());

                if (currentVol + totalToDispense > capacity + 0.1f)
                {
                    float available = capacity - currentVol;
                    float scale = available / totalToDispense;
                    foreach (var (rId, amt) in nextPhase.Ingredients)
                        system.DispenseQueue.Enqueue((rId, (int)Math.Floor(amt.Float() * Math.Max(0, scale))));
                }
                else
                {
                    foreach (var (rId, amt) in nextPhase.Ingredients)
                        system.DispenseQueue.Enqueue((rId, (int)Math.Round(amt.Float())));
                }
            }

            if (system.DispenseQueue.Count == 0 && nextPhase.Ingredients.Count > 0)
            {
                system.Logger.Error("[ABORT] No space!");
                system.CurrentPlan.PhaseQueue.Dequeue();
                return this;
            }

            system.Logger.Info($"[START] {nextPhase.Description}");
            system.TargetProduct = nextPhase.TargetProduct;
            return new WaitDispenserState();
        }
        else if (nextPhase.Type == PhaseType.Wait)
        {
            system.Logger.Info($"[START] {nextPhase.Description}");
            return new WaitReactionState();
        }
        else if (nextPhase.Type == PhaseType.Heat)
        {
            system.Logger.Info($"[START] {nextPhase.Description}");
            return new WaitHeaterState();
        }

        return this;
    }

    public string GetStatusInfo(RobustaAutoChemSystem system) => "Stabilizing...";
    public float GetProgress(RobustaAutoChemSystem system) => GetBaseProgress(system);

    private float GetBaseProgress(RobustaAutoChemSystem system)
    {
        if (system.CurrentPlan == null || system.CurrentPlan.TotalPhases == 0) return 0f;
        int completed = system.CurrentPlan.TotalPhases - system.CurrentPlan.PhaseQueue.Count;
        return (float)completed / system.CurrentPlan.TotalPhases;
    }
}

public sealed class WaitDispenserState : IChemState
{
    public IChemState Execute(float frameTime, RobustaAutoChemSystem system)
    {
        if (system.CurrentPlan == null || system.DispenseQueue.Count == 0) return new CheckNextPhaseState();
        var (firstR, firstAmt) = system.DispenseQueue.Peek();
        system.Dispenser = system.Scanner.FindMachineWithReagent(firstR);
        
        if (system.Dispenser == null) 
        { 
            system.Logger.Warning($"[WAIT] Reagent {firstR} not found! Please add {firstAmt}u manually.");
            system.LastTotalAmountBefore = system.Scanner.GetFullBreakdown(system.Beaker!.Value).Values.Sum();
            return new WaitManualState(); 
        }

        if (system.Beaker == null || !system.Scanner.IsBeakerInsideMachine(system.Dispenser.Value, system.Beaker.Value)) 
        {
            system.StabilizationTimer += 0.5f;
            if (system.StabilizationTimer >= 2.0f)
            {
                system.Logger.Info($"[INFO] Insert beaker into {system.EntManager.GetComponent<MetaDataComponent>(system.Dispenser.Value).EntityName} for {firstR}...");
                system.StabilizationTimer = 0f;
            }
            return this; 
        }
        system.Logger.Info($"[DEBUG] Dispenser ready. Starting to dispense {firstR}.");
        system.StabilizationTimer = 0f;
        return new DispensingState();
    }

    public string GetStatusInfo(RobustaAutoChemSystem system) => "Looking for dispenser...";
    public float GetProgress(RobustaAutoChemSystem system) => GetBaseProgress(system);

    private float GetBaseProgress(RobustaAutoChemSystem system)
    {
        if (system.CurrentPlan == null || system.CurrentPlan.TotalPhases == 0) return 0f;
        int completed = system.CurrentPlan.TotalPhases - system.CurrentPlan.PhaseQueue.Count;
        return (float)completed / system.CurrentPlan.TotalPhases;
    }
}

public sealed class DispensingState : IChemState
{
    public IChemState Execute(float frameTime, RobustaAutoChemSystem system)
    {
        if (system.Beaker == null || system.Dispenser == null || system.CurrentPlan == null) return new CheckNextPhaseState();
        if (system.DispenseQueue.Count > 0)
        {
            var (reagentId, remaining) = system.DispenseQueue.Peek();
            
            if (system.WaitingForDispenseConfirm)
            {
                var currentSnap = system.Scanner.GetFullBreakdown(system.Beaker.Value);
                float currentReagentAmt = currentSnap.TryGetValue(system.LastReagentAdded!, out var rAmt) ? rAmt : 0f;
                float currentProductAmt = (system.TargetProduct != null && currentSnap.TryGetValue(system.TargetProduct, out var pAmt)) ? pAmt : 0f;
                float snapTotal = currentSnap.Values.Sum();

                float reagentIncrease = currentReagentAmt - system.LastReagentAmountBefore;
                float productIncrease = currentProductAmt - system.LastProductAmountBefore;
                float volumeIncrease = snapTotal - system.LastTotalAmountBefore;

                if (reagentIncrease > 0.05f || productIncrease > 0.05f || volumeIncrease > 0.05f)
                {
                    float delta = reagentIncrease;
                    if (productIncrease > 0.05f)
                    {
                        float yield = 1f;
                        float reactantRatio = 1f;
                        if (system.TargetProduct != null)
                        {
                            var recipe = RobustaChemDatabase.GetRecipe(system.TargetProduct);
                            if (recipe != null)
                            {
                                if (recipe.Products.TryGetValue(system.TargetProduct, out var yAmt)) yield = yAmt.Float();
                                if (recipe.Reactants.TryGetValue(system.LastReagentAdded!, out var rData)) reactantRatio = rData.Amount.Float();
                            }
                        }
                        delta = reagentIncrease + (productIncrease * (reactantRatio / yield));
                    }
                    else if (delta < 0.05f && volumeIncrease > 0.05f)
                    {
                        delta = volumeIncrease;
                    }

                    int pouredInt = (int)Math.Round(delta);
                    if (pouredInt < 1) pouredInt = 1; 

                    system.Logger.Info($"[VERIFY] {system.LastReagentAdded}: dispensed {pouredInt}u (measured {delta:F1}u).");
                    system.WaitingForDispenseConfirm = false;
                    
                    var list = system.DispenseQueue.ToList();
                    var head = list[0];
                    int newRemaining = head.Amount - pouredInt;
                    list.RemoveAt(0);
                    if (newRemaining > 0) list.Insert(0, (reagentId, newRemaining));
                    system.DispenseQueue = new Queue<(string, int)>(list);
                    
                    system.LogBeakerContents();
                    return this; 
                }
                return this; 
            }

            var jugLoc = system.Scanner.FindJugLocation(system.Dispenser.Value, reagentId);
            if (jugLoc == null) { system.Dispenser = null; return new WaitDispenserState(); }

            ReagentDispenserDispenseAmount dose;
            if (remaining >= 120) dose = ReagentDispenserDispenseAmount.U120;
            else if (remaining >= 60) dose = ReagentDispenserDispenseAmount.U60;
            else if (remaining >= 40) dose = ReagentDispenserDispenseAmount.U40;
            else if (remaining >= 30) dose = ReagentDispenserDispenseAmount.U30;
            else if (remaining >= 20) dose = ReagentDispenserDispenseAmount.U20;
            else if (remaining >= 15) dose = ReagentDispenserDispenseAmount.U15;
            else if (remaining >= 10) dose = ReagentDispenserDispenseAmount.U10;
            else if (remaining >= 5) dose = ReagentDispenserDispenseAmount.U5;
            else dose = ReagentDispenserDispenseAmount.U1;

            if (system.LastActiveDose != dose)
            {
                system.Logger.Info($"[SYNC] Setting dose {dose}...");
                system.NetworkHands.SendBuiMessage(system.Dispenser.Value, new ReagentDispenserSetDispenseAmountMessage(dose));
                system.LastActiveDose = dose;
                return this;
            }

            var snapBefore = system.Scanner.GetFullBreakdown(system.Beaker.Value);
            system.LastReagentAdded = reagentId;
            system.LastRequestedDose = (int)dose; 
            system.LastReagentAmountBefore = snapBefore.TryGetValue(reagentId, out var rOld) ? rOld : 0f;
            system.LastTotalAmountBefore = snapBefore.Values.Sum();
            system.LastProductAmountBefore = (system.TargetProduct != null && snapBefore.TryGetValue(system.TargetProduct, out var pOld)) ? pOld : 0f;
            system.WaitingForDispenseConfirm = true;

            system.Logger.Info($"[ACTION] Dispensing {reagentId} (attempting {dose}u). Remaining in plan: {remaining}u");
            system.NetworkHands.SendBuiMessage(system.Dispenser.Value, new ReagentDispenserDispenseReagentMessage(jugLoc.Value));
            return this;
        }
        else
        {
            system.LastActiveDose = null; 
            
            bool nextIsHeat = system.CurrentPlan.PhaseQueue.Count > 1 && system.CurrentPlan.PhaseQueue.ElementAt(1).Type == PhaseType.Heat;
            if (nextIsHeat || system.CurrentPlan.PhaseQueue.Count == 1)
                system.NetworkHands.EjectViaUI(system.Dispenser.Value);

            system.CurrentPlan.PhaseQueue.Dequeue(); 
            return new CheckNextPhaseState(); 
        }
    }

    public string GetStatusInfo(RobustaAutoChemSystem system) => system.DispenseQueue.Count > 0 ? $"Adding {system.DispenseQueue.Peek().Reagent}" : "Complete";
    public float GetProgress(RobustaAutoChemSystem system) => GetBaseProgress(system);

    private float GetBaseProgress(RobustaAutoChemSystem system)
    {
        if (system.CurrentPlan == null || system.CurrentPlan.TotalPhases == 0) return 0f;
        int completed = system.CurrentPlan.TotalPhases - system.CurrentPlan.PhaseQueue.Count;
        return (float)completed / system.CurrentPlan.TotalPhases;
    }
}

public sealed class WaitManualState : IChemState
{
    public IChemState Execute(float frameTime, RobustaAutoChemSystem system)
    {
        if (system.Beaker == null || system.CurrentPlan == null) return this;
        if (system.DispenseQueue.Count == 0) 
        { 
            system.CurrentPlan.PhaseQueue.Dequeue(); 
            return new CheckNextPhaseState(); 
        }
        
        var (mR, mN) = system.DispenseQueue.Peek();
        var curV = system.Scanner.GetFullBreakdown(system.Beaker.Value).Values.Sum();
        float added = curV - system.LastTotalAmountBefore;

        if (system.Scanner.GetReagentAmount(system.Beaker.Value, mR) >= mN || added >= (mN - 0.1f)) 
        { 
            system.Logger.Info($"[VERIFY] Manual dispensing of {mR} confirmed.");
            system.DispenseQueue.Dequeue(); 
            system.LastTotalAmountBefore = curV; 
            
            if (system.DispenseQueue.Count == 0)
            {
                system.CurrentPlan.PhaseQueue.Dequeue(); 
                return new CheckNextPhaseState();
            }
            else
            {
                return new WaitDispenserState(); 
            }
        }
        return this;
    }

    public string GetStatusInfo(RobustaAutoChemSystem system) => system.DispenseQueue.Count > 0 ? $"MANUAL: {system.DispenseQueue.Peek().Reagent} ({system.DispenseQueue.Peek().Amount}u)" : "Manual done";
    public float GetProgress(RobustaAutoChemSystem system) => GetBaseProgress(system);

    private float GetBaseProgress(RobustaAutoChemSystem system)
    {
        if (system.CurrentPlan == null || system.CurrentPlan.TotalPhases == 0) return 0f;
        int completed = system.CurrentPlan.TotalPhases - system.CurrentPlan.PhaseQueue.Count;
        return (float)completed / system.CurrentPlan.TotalPhases;
    }
}

public sealed class WaitReactionState : IChemState
{
    public IChemState Execute(float frameTime, RobustaAutoChemSystem system)
    {
        if (system.Beaker == null || system.CurrentPlan == null || system.CurrentPlan.PhaseQueue.Count == 0) return this;
        var wPhase = system.CurrentPlan.PhaseQueue.Peek();
        var contents = system.Scanner.GetFullBreakdown(system.Beaker.Value);
        
        bool allDone = true;
        foreach (var r in wPhase.WaitReagents)
        {
            if (!contents.ContainsKey(r) || contents[r] < 0.1f)
            {
                bool foundInFuture = false;
                foreach (var futurePhase in system.CurrentPlan.PhaseQueue.Skip(1))
                {
                    if (futurePhase.TargetProduct != null && contents.ContainsKey(futurePhase.TargetProduct) && contents[futurePhase.TargetProduct] > 0.1f)
                    {
                        foundInFuture = true;
                        break;
                    }
                }

                if (!foundInFuture)
                {
                    allDone = false;
                    break;
                }
            }
        }

        if (allDone)
        {
            system.Logger.Info($"[SUCCESS] Reagents obtained or already processed.");
            system.CurrentPlan.PhaseQueue.Dequeue();
            system.StabilizationTimer = 0f;
            system.LastTotalAmountBefore = contents.Values.Sum();
            return new CheckNextPhaseState();
        }
        else
        {
            system.StabilizationTimer += 0.5f;
            if (system.StabilizationTimer >= 2.0f)
            {
                system.Logger.Info($"[WAIT] Waiting for reaction {string.Join(", ", wPhase.WaitReagents)}... (Volume: {contents.Values.Sum():F1}u)");
                system.StabilizationTimer = 0f;
            }
        }
        return this;
    }

    public string GetStatusInfo(RobustaAutoChemSystem system)
    {
        if (system.CurrentPlan == null || system.CurrentPlan.PhaseQueue.Count == 0) return "Reaction done";
        return $"Reacting: {string.Join(", ", system.CurrentPlan.PhaseQueue.Peek().WaitReagents)}";
    }
    public float GetProgress(RobustaAutoChemSystem system) => GetBaseProgress(system);

    private float GetBaseProgress(RobustaAutoChemSystem system)
    {
        if (system.CurrentPlan == null || system.CurrentPlan.TotalPhases == 0) return 0f;
        int completed = system.CurrentPlan.TotalPhases - system.CurrentPlan.PhaseQueue.Count;
        return (float)completed / system.CurrentPlan.TotalPhases;
    }
}

public sealed class WaitHeaterState : IChemState
{
    public IChemState Execute(float frameTime, RobustaAutoChemSystem system)
    {
        if (system.Beaker == null) return this;
        var heater = system.Scanner.FindMachine("heater");
        if (heater != null && system.Scanner.IsBeakerInsideMachine(heater.Value, system.Beaker.Value)) 
        {
            return new HeatingState();
        }
        return this;
    }

    public string GetStatusInfo(RobustaAutoChemSystem system) => "Wait for Heater...";
    public float GetProgress(RobustaAutoChemSystem system) => GetBaseProgress(system);

    private float GetBaseProgress(RobustaAutoChemSystem system)
    {
        if (system.CurrentPlan == null || system.CurrentPlan.TotalPhases == 0) return 0f;
        int completed = system.CurrentPlan.TotalPhases - system.CurrentPlan.PhaseQueue.Count;
        return (float)completed / system.CurrentPlan.TotalPhases;
    }
}

public sealed class HeatingState : IChemState
{
    public IChemState Execute(float frameTime, RobustaAutoChemSystem system)
    {
        if (system.Beaker == null || system.CurrentPlan == null || system.CurrentPlan.PhaseQueue.Count == 0) return this;
        var hPhase = system.CurrentPlan.PhaseQueue.Peek();
        var t = system.Scanner.GetBeakerTemperature(system.Beaker.Value) ?? system.LastNetworkTemp;
        
        if (t >= hPhase.TargetTemperature)
        {
            if (!system.TargetTempReached)
            {
                system.Logger.Info($">>> {hPhase.TargetTemperature}K REACHED! REMOVE BEAKER! <<<");
                system.TargetTempReached = true;
            }
        }

        var heaterNow = system.Scanner.FindMachine("heater");
        if (heaterNow == null || !system.Scanner.IsBeakerInsideMachine(heaterNow.Value, system.Beaker.Value))
        {
            system.Logger.Info("[DEBUG] Beaker removed from heater.");
            if (system.TargetTempReached)
            {
                system.CurrentPlan.PhaseQueue.Dequeue();
                return new CheckNextPhaseState();
            }
            else
            {
                system.Logger.Warning("[WARN] Beaker removed prematurely! Returning to heater wait.");
                return new WaitHeaterState();
            }
        }
        return this;
    }

    public string GetStatusInfo(RobustaAutoChemSystem system)
    {
        if (system.CurrentPlan == null || system.CurrentPlan.PhaseQueue.Count == 0) return "Heating done";
        var hPhase = system.CurrentPlan.PhaseQueue.Peek();
        var curT = (system.Beaker != null) ? (system.Scanner.GetBeakerTemperature(system.Beaker.Value) ?? system.LastNetworkTemp ?? 0) : 0;
        return system.TargetTempReached ? "DONE! Eject beaker!" : $"Heating: {curT:F1}K / {hPhase.TargetTemperature}K";
    }

    public float GetProgress(RobustaAutoChemSystem system)
    {
        if (system.CurrentPlan == null || system.CurrentPlan.TotalPhases == 0) return 0f;
        int completed = system.CurrentPlan.TotalPhases - system.CurrentPlan.PhaseQueue.Count;
        float baseProgress = (float)completed / system.CurrentPlan.TotalPhases;
        float phaseWeight = 1f / system.CurrentPlan.TotalPhases;

        if (system.CurrentPlan.PhaseQueue.Count > 0)
        {
            var phase = system.CurrentPlan.PhaseQueue.Peek();
            var curT = (system.Beaker != null) ? (system.Scanner.GetBeakerTemperature(system.Beaker.Value) ?? system.LastNetworkTemp ?? 293.15f) : 293.15f;
            float tProgress = Math.Clamp((curT - 293.15f) / (phase.TargetTemperature - 293.15f), 0f, 1f);
            return baseProgress + (tProgress * phaseWeight);
        }
        return baseProgress;
    }
}

public sealed class FinishState : IChemState
{
    public IChemState Execute(float frameTime, RobustaAutoChemSystem system)
    {
        if (system.ExamineSyncTimer > 1.0f)
        {
            system.ExamineSyncTimer = 0f;
            system.LogBeakerContents();
            system.Logger.Info("[AutoChem] Done!");
            return new IdleState();
        }
        return this;
    }

    public string GetStatusInfo(RobustaAutoChemSystem system) => "ALL DONE! Take beaker.";
    public float GetProgress(RobustaAutoChemSystem system) => 1.0f;
}
