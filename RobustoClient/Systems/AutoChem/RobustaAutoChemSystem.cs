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

    private AutoChemContext _context = default!;
    public IChemState CurrentState { get; private set; } = IdleState.Instance;

    public static readonly Regex TempRegex = new(@"(\d+(\.\d+)?)\s*K", RegexOptions.Compiled);

    public override void Initialize()
    {
        base.Initialize();
        var sawmill = Robust.Shared.Log.Logger.GetSawmill("autochem");
        _context = new AutoChemContext(_scanner, _networkHands, EntityManager, sawmill);
    }

    public void StartJob(string reagent, FixedPoint2 amount)
    {
        _context.Beaker = _scanner.GetBeakerInHand();
        float capacity = 50f;
        if (_context.Beaker != null) capacity = _scanner.GetBeakerCapacity(_context.Beaker.Value);

        _context.CurrentPlan = RobustaRecipeSolver.CreatePlan(reagent, amount, capacity);
        
        if (_context.CurrentPlan == null) 
        {
            _context.Logger.Error($"[DEBUG] ERROR: Recipe for {reagent} not found!");
            return; 
        }

        _context.Logger.Info(_context.CurrentPlan.GetPlanSummary());

        _context.Reset();
        CurrentState = CheckNextPhaseState.Instance;
        _context.Logger.Info("[DEBUG] Auto-chemist ready.");
    }

    public void StopJob()
    {
        CurrentState = IdleState.Instance;
        _context.CurrentPlan = null;
        _context.Dispenser = null;
        _context.Beaker = null;
        _context.Logger.Info("[AutoChem] Operation stopped.");
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (CurrentState == IdleState.Instance || _context.CurrentPlan == null) return;

        _context.Timer += frameTime;
        _context.ExamineSyncTimer += frameTime;

        if (_context.Timer < 0.5f) return;
        _context.Timer = 0f;

        if (_context.Beaker == null || !EntityManager.EntityExists(_context.Beaker.Value))
        {
            _context.Beaker = _scanner.GetBeakerInHand();
            if (_context.Beaker == null) return;
            _context.Logger.Info($"[DEBUG] Beaker detected.");
            _context.LogBeakerContents();
        }

        CurrentState = CurrentState.Execute(frameTime, _context);
    }

    public string GetStatusInfo() => CurrentState.GetStatusInfo(_context);
    public float GetProgress() => CurrentState.GetProgress(_context);

    // Context Accessors for UI or external systems if needed
    public AutoChemContext Context => _context;
}

// ==========================================
// STATE IMPLEMENTATIONS
// ==========================================

public sealed class IdleState : ChemStateBase
{
    public static readonly IdleState Instance = new();
    private IdleState() { }

    public override IChemState Execute(float frameTime, AutoChemContext context) => this;
    public override string GetStatusInfo(AutoChemContext context) => "Idle";
    public override float GetProgress(AutoChemContext context) => 0f;
}

public sealed class CheckNextPhaseState : ChemStateBase
{
    public static readonly CheckNextPhaseState Instance = new();
    private CheckNextPhaseState() { }

    public override IChemState Execute(float frameTime, AutoChemContext context)
    {
        if (context.Beaker == null || context.CurrentPlan == null) return this;

        if (context.CurrentPlan.PhaseQueue.Count == 0)
        {
            context.Logger.Info("[DEBUG] Plan completed.");
            return FinishState.Instance;
        }

        var nextPhase = context.CurrentPlan.PhaseQueue.Peek();
        var currentContents = context.Scanner.GetFullBreakdown(context.Beaker.Value);
        var currentVol = currentContents.Values.Sum();
        var capacity = context.Scanner.GetBeakerCapacity(context.Beaker.Value);

        if (currentVol > capacity + 0.5f)
        {
            context.Logger.Error($"[OVERFLOW] Volume {currentVol:F1}u exceeds beaker limit {capacity}u! STOPPING.");
            context.CurrentPlan = null; // Equivalent to StopJob logic
            return IdleState.Instance;
        }

        if (Math.Abs(currentVol - context.LastTotalAmountBefore) > 0.1f)
        {
            context.StabilizationTimer += 0.5f;
            if (context.StabilizationTimer < 2.0f) 
            {
                context.Logger.Info($"[WAIT] Stabilizing solution... ({context.LastTotalAmountBefore:F1}u -> {currentVol:F1}u)");
                context.LastTotalAmountBefore = currentVol;
                return this; 
            }
        }

        context.StabilizationTimer = 0f;
        context.TargetTempReached = false;
        context.LastTotalAmountBefore = currentVol;

        // SMART SKIP: Only for wait and heat phases
        if (nextPhase.Type != PhaseType.Dispense)
        {
            // If product of this phase is already in the beaker
            if (nextPhase.TargetProduct != null && currentContents.ContainsKey(nextPhase.TargetProduct) && currentContents[nextPhase.TargetProduct] > 0.1f)
            {
                context.Logger.Info($"[SKIP] Phase '{nextPhase.Description}' skipped because {nextPhase.TargetProduct} is already obtained.");
                context.CurrentPlan.PhaseQueue.Dequeue();
                return this; 
            }

            // Check for products from FUTURE phases (global skip for Wait/Heat)
            foreach (var futurePhase in context.CurrentPlan.PhaseQueue.Skip(1))
            {
                if (futurePhase.TargetProduct != null && currentContents.ContainsKey(futurePhase.TargetProduct) && currentContents[futurePhase.TargetProduct] > 0.1f)
                {
                    context.Logger.Info($"[SKIP] Phase '{nextPhase.Description}' skipped because a product of a future phase was detected: {futurePhase.TargetProduct}");
                    context.CurrentPlan.PhaseQueue.Dequeue();
                    return this;
                }
            }
        }

        if (nextPhase.Type == PhaseType.Dispense)
        {
            if (context.DispenseQueue.Count == 0)
            {
                float totalToDispense = nextPhase.Ingredients.Values.Sum(v => v.Float());

                if (currentVol + totalToDispense > capacity + 0.1f)
                {
                    float available = capacity - currentVol;
                    float scale = available / totalToDispense;
                    foreach (var (rId, amt) in nextPhase.Ingredients)
                        context.DispenseQueue.Enqueue((rId, (int)Math.Floor(amt.Float() * Math.Max(0, scale))));
                }
                else
                {
                    foreach (var (rId, amt) in nextPhase.Ingredients)
                        context.DispenseQueue.Enqueue((rId, (int)Math.Round(amt.Float())));
                }
            }

            if (context.DispenseQueue.Count == 0 && nextPhase.Ingredients.Count > 0)
            {
                context.Logger.Error("[ABORT] No space!");
                context.CurrentPlan.PhaseQueue.Dequeue();
                return this;
            }

            context.Logger.Info($"[START] {nextPhase.Description}");
            context.TargetProduct = nextPhase.TargetProduct;
            return WaitDispenserState.Instance;
        }
        else if (nextPhase.Type == PhaseType.Wait)
        {
            context.Logger.Info($"[START] {nextPhase.Description}");
            return WaitReactionState.Instance;
        }
        else if (nextPhase.Type == PhaseType.Heat)
        {
            context.Logger.Info($"[START] {nextPhase.Description}");
            return WaitHeaterState.Instance;
        }

        return this;
    }

    public override string GetStatusInfo(AutoChemContext context) => "Stabilizing...";
}

public sealed class WaitDispenserState : ChemStateBase
{
    public static readonly WaitDispenserState Instance = new();
    private WaitDispenserState() { }

    public override IChemState Execute(float frameTime, AutoChemContext context)
    {
        if (context.CurrentPlan == null || context.DispenseQueue.Count == 0) return CheckNextPhaseState.Instance;
        var (firstR, firstAmt) = context.DispenseQueue.Peek();
        context.Dispenser = context.Scanner.FindMachineWithReagent(firstR);
        
        if (context.Dispenser == null) 
        { 
            context.Logger.Warning($"[WAIT] Reagent {firstR} not found! Please add {firstAmt}u manually.");
            context.LastTotalAmountBefore = context.Scanner.GetFullBreakdown(context.Beaker!.Value).Values.Sum();
            return WaitManualState.Instance; 
        }

        if (context.Beaker == null || !context.Scanner.IsBeakerInsideMachine(context.Dispenser.Value, context.Beaker.Value)) 
        {
            context.StabilizationTimer += 0.5f;
            if (context.StabilizationTimer >= 2.0f)
            {
                context.Logger.Info($"[INFO] Insert beaker into {context.EntManager.GetComponent<MetaDataComponent>(context.Dispenser.Value).EntityName} for {firstR}...");
                context.StabilizationTimer = 0f;
            }
            return this; 
        }
        context.Logger.Info($"[DEBUG] Dispenser ready. Starting to dispense {firstR}.");
        context.StabilizationTimer = 0f;
        return DispensingState.Instance;
    }

    public override string GetStatusInfo(AutoChemContext context) => "Looking for dispenser...";
}

public sealed class DispensingState : ChemStateBase
{
    public static readonly DispensingState Instance = new();
    private DispensingState() { }

    public override IChemState Execute(float frameTime, AutoChemContext context)
    {
        if (context.Beaker == null || context.Dispenser == null || context.CurrentPlan == null) return CheckNextPhaseState.Instance;
        if (context.DispenseQueue.Count > 0)
        {
            var (reagentId, remaining) = context.DispenseQueue.Peek();
            
            if (context.WaitingForDispenseConfirm)
            {
                var currentSnap = context.Scanner.GetFullBreakdown(context.Beaker.Value);
                float currentReagentAmt = currentSnap.TryGetValue(context.LastReagentAdded!, out var rAmt) ? rAmt : 0f;
                float currentProductAmt = (context.TargetProduct != null && currentSnap.TryGetValue(context.TargetProduct, out var pAmt)) ? pAmt : 0f;
                float snapTotal = currentSnap.Values.Sum();

                float reagentIncrease = currentReagentAmt - context.LastReagentAmountBefore;
                float productIncrease = currentProductAmt - context.LastProductAmountBefore;
                float volumeIncrease = snapTotal - context.LastTotalAmountBefore;

                if (reagentIncrease > 0.05f || productIncrease > 0.05f || volumeIncrease > 0.05f)
                {
                    float delta = reagentIncrease;
                    if (productIncrease > 0.05f)
                    {
                        float yield = 1f;
                        float reactantRatio = 1f;
                        if (context.TargetProduct != null)
                        {
                            var recipe = RobustaChemDatabase.GetRecipe(context.TargetProduct);
                            if (recipe != null)
                            {
                                if (recipe.Products.TryGetValue(context.TargetProduct, out var yAmt)) yield = yAmt.Float();
                                if (recipe.Reactants.TryGetValue(context.LastReagentAdded!, out var rData)) reactantRatio = rData.Amount.Float();
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

                    context.Logger.Info($"[VERIFY] {context.LastReagentAdded}: dispensed {pouredInt}u (measured {delta:F1}u).");
                    context.WaitingForDispenseConfirm = false;
                    
                    var list = context.DispenseQueue.ToList();
                    var head = list[0];
                    int newRemaining = head.Amount - pouredInt;
                    list.RemoveAt(0);
                    if (newRemaining > 0) list.Insert(0, (reagentId, newRemaining));
                    context.DispenseQueue = new Queue<(string, int)>(list);
                    
                    context.LogBeakerContents();
                    return this; 
                }
                return this; 
            }

            var jugLoc = context.Scanner.FindJugLocation(context.Dispenser.Value, reagentId);
            if (jugLoc == null) { context.Dispenser = null; return WaitDispenserState.Instance; }

            // Determine best dose based on what's actually available on this machine
            var availableDoses = context.Scanner.GetAvailableDoses(context.Dispenser.Value);
            ReagentDispenserDispenseAmount dose;
            
            if (remaining >= 120 && availableDoses.Contains(ReagentDispenserDispenseAmount.U120)) dose = ReagentDispenserDispenseAmount.U120;
            else if (remaining >= 60 && availableDoses.Contains(ReagentDispenserDispenseAmount.U60)) dose = ReagentDispenserDispenseAmount.U60;
            else if (remaining >= 40 && availableDoses.Contains(ReagentDispenserDispenseAmount.U40)) dose = ReagentDispenserDispenseAmount.U40;
            else if (remaining >= 30 && availableDoses.Contains(ReagentDispenserDispenseAmount.U30)) dose = ReagentDispenserDispenseAmount.U30;
            else if (remaining >= 20 && availableDoses.Contains(ReagentDispenserDispenseAmount.U20)) dose = ReagentDispenserDispenseAmount.U20;
            else if (remaining >= 15 && availableDoses.Contains(ReagentDispenserDispenseAmount.U15)) dose = ReagentDispenserDispenseAmount.U15;
            else if (remaining >= 10 && availableDoses.Contains(ReagentDispenserDispenseAmount.U10)) dose = ReagentDispenserDispenseAmount.U10;
            else if (remaining >= 5 && availableDoses.Contains(ReagentDispenserDispenseAmount.U5)) dose = ReagentDispenserDispenseAmount.U5;
            else dose = ReagentDispenserDispenseAmount.U1; // Guaranteed fallback

            if (context.LastActiveDose != dose)
            {
                context.Logger.Info($"[SYNC] Setting dose {dose}...");
                context.NetworkHands.SendBuiMessage(context.Dispenser.Value, new ReagentDispenserSetDispenseAmountMessage(dose));
                context.LastActiveDose = dose;
                return this;
            }

            var snapBefore = context.Scanner.GetFullBreakdown(context.Beaker.Value);
            context.LastReagentAdded = reagentId;
            context.LastRequestedDose = (int)dose; 
            context.LastReagentAmountBefore = snapBefore.TryGetValue(reagentId, out var rOld) ? rOld : 0f;
            context.LastTotalAmountBefore = snapBefore.Values.Sum();
            context.LastProductAmountBefore = (context.TargetProduct != null && snapBefore.TryGetValue(context.TargetProduct, out var pOld)) ? pOld : 0f;
            context.WaitingForDispenseConfirm = true;

            context.Logger.Info($"[ACTION] Dispensing {reagentId} (using {dose}u). Remaining: {remaining}u");
            context.NetworkHands.SendBuiMessage(context.Dispenser.Value, new ReagentDispenserDispenseReagentMessage(jugLoc.Value));
            return this;
        }
        else
        {
            context.LastActiveDose = null; 
            
            bool nextIsHeat = context.CurrentPlan.PhaseQueue.Count > 1 && context.CurrentPlan.PhaseQueue.ElementAt(1).Type == PhaseType.Heat;
            if (nextIsHeat || context.CurrentPlan.PhaseQueue.Count == 1)
            {
                context.Logger.Info("[DEBUG] Dispensing phase complete. Ejecting beaker.");
                context.NetworkHands.EjectViaUI(context.Dispenser.Value);
            }

            context.CurrentPlan.PhaseQueue.Dequeue(); 
            return CheckNextPhaseState.Instance; 
        }
    }

    public override string GetStatusInfo(AutoChemContext context) => context.DispenseQueue.Count > 0 ? $"Adding {context.DispenseQueue.Peek().Reagent}" : "Complete";
}

public sealed class WaitManualState : ChemStateBase
{
    public static readonly WaitManualState Instance = new();
    private WaitManualState() { }

    public override IChemState Execute(float frameTime, AutoChemContext context)
    {
        if (context.Beaker == null || context.CurrentPlan == null) return this;
        if (context.DispenseQueue.Count == 0) 
        { 
            context.CurrentPlan.PhaseQueue.Dequeue(); 
            return CheckNextPhaseState.Instance; 
        }
        
        var (mR, mN) = context.DispenseQueue.Peek();
        var curV = context.Scanner.GetFullBreakdown(context.Beaker.Value).Values.Sum();
        float added = curV - context.LastTotalAmountBefore;

        if (context.Scanner.GetReagentAmount(context.Beaker.Value, mR) >= mN || added >= (mN - 0.1f)) 
        { 
            context.Logger.Info($"[VERIFY] Manual dispensing of {mR} confirmed.");
            context.DispenseQueue.Dequeue(); 
            context.LastTotalAmountBefore = curV; 
            
            if (context.DispenseQueue.Count == 0)
            {
                context.CurrentPlan.PhaseQueue.Dequeue(); 
                return CheckNextPhaseState.Instance;
            }
            else
            {
                return WaitDispenserState.Instance; 
            }
        }
        return this;
    }

    public override string GetStatusInfo(AutoChemContext context) => context.DispenseQueue.Count > 0 ? $"MANUAL: {context.DispenseQueue.Peek().Reagent} ({context.DispenseQueue.Peek().Amount}u)" : "Manual done";
}

public sealed class WaitReactionState : ChemStateBase
{
    public static readonly WaitReactionState Instance = new();
    private WaitReactionState() { }

    public override IChemState Execute(float frameTime, AutoChemContext context)
    {
        if (context.Beaker == null || context.CurrentPlan == null || context.CurrentPlan.PhaseQueue.Count == 0) return this;
        var wPhase = context.CurrentPlan.PhaseQueue.Peek();
        var contents = context.Scanner.GetFullBreakdown(context.Beaker.Value);
        
        bool allDone = true;
        foreach (var r in wPhase.WaitReagents)
        {
            if (!contents.ContainsKey(r) || contents[r] < 0.1f)
            {
                bool foundInFuture = false;
                foreach (var futurePhase in context.CurrentPlan.PhaseQueue.Skip(1))
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
            context.Logger.Info($"[SUCCESS] Reagents obtained or already processed.");
            context.CurrentPlan.PhaseQueue.Dequeue();
            context.StabilizationTimer = 0f;
            context.LastTotalAmountBefore = contents.Values.Sum();
            return CheckNextPhaseState.Instance;
        }
        else
        {
            context.StabilizationTimer += 0.5f;
            if (context.StabilizationTimer >= 2.0f)
            {
                context.Logger.Info($"[WAIT] Waiting for reaction {string.Join(", ", wPhase.WaitReagents)}... (Volume: {contents.Values.Sum():F1}u)");
                context.StabilizationTimer = 0f;
            }
        }
        return this;
    }

    public override string GetStatusInfo(AutoChemContext context)
    {
        if (context.CurrentPlan == null || context.CurrentPlan.PhaseQueue.Count == 0) return "Reaction done";
        return $"Reacting: {string.Join(", ", context.CurrentPlan.PhaseQueue.Peek().WaitReagents)}";
    }
}

public sealed class WaitHeaterState : ChemStateBase
{
    public static readonly WaitHeaterState Instance = new();
    private WaitHeaterState() { }

    public override IChemState Execute(float frameTime, AutoChemContext context)
    {
        if (context.Beaker == null) return this;
        var heater = context.Scanner.FindMachine("heater");
        if (heater != null && context.Scanner.IsBeakerInsideMachine(heater.Value, context.Beaker.Value)) 
        {
            return HeatingState.Instance;
        }
        return this;
    }

    public override string GetStatusInfo(AutoChemContext context) => "Wait for Heater...";
}

public sealed class HeatingState : ChemStateBase
{
    public static readonly HeatingState Instance = new();
    private HeatingState() { }

    public override IChemState Execute(float frameTime, AutoChemContext context)
    {
        if (context.Beaker == null || context.CurrentPlan == null || context.CurrentPlan.PhaseQueue.Count == 0) return this;
        var hPhase = context.CurrentPlan.PhaseQueue.Peek();
        var t = context.Scanner.GetBeakerTemperature(context.Beaker.Value) ?? context.LastNetworkTemp;
        
        if (t >= hPhase.TargetTemperature)
        {
            if (!context.TargetTempReached)
            {
                context.Logger.Info($">>> {hPhase.TargetTemperature}K REACHED! REMOVE BEAKER! <<<");
                context.TargetTempReached = true;
            }
        }

        var heaterNow = context.Scanner.FindMachine("heater");
        if (heaterNow == null || !context.Scanner.IsBeakerInsideMachine(heaterNow.Value, context.Beaker.Value))
        {
            context.Logger.Info("[DEBUG] Beaker removed from heater.");
            if (context.TargetTempReached)
            {
                context.CurrentPlan.PhaseQueue.Dequeue();
                return CheckNextPhaseState.Instance;
            }
            else
            {
                context.Logger.Warning("[WARN] Beaker removed prematurely! Returning to heater wait.");
                return WaitHeaterState.Instance;
            }
        }
        return this;
    }

    public override string GetStatusInfo(AutoChemContext context)
    {
        if (context.CurrentPlan == null || context.CurrentPlan.PhaseQueue.Count == 0) return "Heating done";
        var hPhase = context.CurrentPlan.PhaseQueue.Peek();
        var curT = (context.Beaker != null) ? (context.Scanner.GetBeakerTemperature(context.Beaker.Value) ?? context.LastNetworkTemp ?? 0) : 0;
        return context.TargetTempReached ? "DONE! Eject beaker!" : $"Heating: {curT:F1}K / {hPhase.TargetTemperature}K";
    }

    public override float GetProgress(AutoChemContext context)
    {
        if (context.CurrentPlan == null || context.CurrentPlan.TotalPhases == 0) return 0f;
        int completed = context.CurrentPlan.TotalPhases - context.CurrentPlan.PhaseQueue.Count;
        float baseProgress = (float)completed / context.CurrentPlan.TotalPhases;
        float phaseWeight = 1f / context.CurrentPlan.TotalPhases;

        if (context.CurrentPlan.PhaseQueue.Count > 0)
        {
            var phase = context.CurrentPlan.PhaseQueue.Peek();
            var curT = (context.Beaker != null) ? (context.Scanner.GetBeakerTemperature(context.Beaker.Value) ?? context.LastNetworkTemp ?? 293.15f) : 293.15f;
            float tProgress = Math.Clamp((curT - 293.15f) / (phase.TargetTemperature - 293.15f), 0f, 1f);
            return baseProgress + (tProgress * phaseWeight);
        }
        return baseProgress;
    }
}

public sealed class FinishState : ChemStateBase
{
    public static readonly FinishState Instance = new();
    private FinishState() { }

    public override IChemState Execute(float frameTime, AutoChemContext context)
    {
        if (context.ExamineSyncTimer > 1.0f)
        {
            context.ExamineSyncTimer = 0f;
            context.LogBeakerContents();
            context.Logger.Info("[AutoChem] Done!");
            return IdleState.Instance;
        }
        return this;
    }

    public override string GetStatusInfo(AutoChemContext context) => "ALL DONE! Take beaker.";
    public override float GetProgress(AutoChemContext context) => 1.0f;
}
