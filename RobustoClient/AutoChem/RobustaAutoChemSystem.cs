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

public enum AutoChemState
{
    Idle,
    CheckNextPhase,
    WaitDispenser,
    Dispensing,
    WaitManual,
    WaitReaction,
    WaitHeater,
    Heating,
    Finish
}

public sealed class RobustaAutoChemSystem : EntitySystem
{
    [Dependency] private readonly RobustaWorldScanner _scanner = default!;
    [Dependency] private readonly RobustaNetworkHands _networkHands = default!;

    private ISawmill _sawmill = default!;
    public AutoChemState CurrentState { get; private set; } = AutoChemState.Idle;

    private DispensePlan? _currentPlan;
    
    private EntityUid? _dispenser;
    private EntityUid? _beaker;

    private float _timer = 0f;
    private float _examineSyncTimer = 0f;
    private float _stabilizationTimer = 0f;
    private float? _lastNetworkTemp;
    private bool _targetTempReached = false;
    private Queue<(string Reagent, int Amount)> _dispenseQueue = new();

    private string? _lastReagentAdded;
    private string? _targetProduct;
    private int _lastRequestedDose;
    private float _lastReagentAmountBefore;
    private float _lastTotalAmountBefore;
    private float _lastProductAmountBefore;
    private ReagentDispenserDispenseAmount? _lastActiveDose;
    private bool _waitingForDispenseConfirm = false;

    private static readonly Regex TempRegex = new(@"(\d+(\.\d+)?)\s*K", RegexOptions.Compiled);

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = Logger.GetSawmill("autochem");
    }

    public void StartJob(string reagent, FixedPoint2 amount)
    {
        _beaker = _scanner.GetBeakerInHand();
        float capacity = 50f;
        if (_beaker != null) capacity = _scanner.GetBeakerCapacity(_beaker.Value);

        _currentPlan = RobustaRecipeSolver.CreatePlan(reagent, amount, capacity);
        
        if (_currentPlan == null) 
        {
            _sawmill.Error($"[DEBUG] ERROR: Recipe for {reagent} not found!");
            return; 
        }

        _sawmill.Info(_currentPlan.GetPlanSummary());

        CurrentState = AutoChemState.CheckNextPhase;
        _timer = 0f;
        _examineSyncTimer = 0f;
        _stabilizationTimer = 0f;
        _lastNetworkTemp = null;
        _targetTempReached = false;
        _dispenseQueue.Clear();
        _waitingForDispenseConfirm = false;
        _lastActiveDose = null;
        _lastTotalAmountBefore = 0f;
        _lastProductAmountBefore = 0f;
        _lastReagentAmountBefore = 0f;
        _sawmill.Info("[DEBUG] Auto-chemist ready.");
    }

    public void StopJob()
    {
        CurrentState = AutoChemState.Idle;
        _currentPlan = null;
        _dispenser = null;
        _beaker = null;
        _sawmill.Info("[AutoChem] Operation stopped.");
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (CurrentState == AutoChemState.Idle || _currentPlan == null) return;

        _timer += frameTime;
        _examineSyncTimer += frameTime;

        if (_timer < 0.5f) return;
        _timer = 0f;

        if (_beaker == null || !EntityManager.EntityExists(_beaker.Value))
        {
            _beaker = _scanner.GetBeakerInHand();
            if (_beaker == null) return;
            _sawmill.Info($"[DEBUG] Beaker detected.");
            LogBeakerContents();
        }

        switch (CurrentState)
        {
            case AutoChemState.CheckNextPhase:
                if (_beaker == null || _currentPlan == null) return;

                if (_currentPlan.PhaseQueue.Count == 0)
                {
                    _sawmill.Info("[DEBUG] Plan completed.");
                    CurrentState = AutoChemState.Finish;
                    return;
                }

                var nextPhase = _currentPlan.PhaseQueue.Peek();
                var currentContents = _scanner.GetFullBreakdown(_beaker.Value);
                var currentVol = currentContents.Values.Sum();
                var capacity = _scanner.GetBeakerCapacity(_beaker.Value);

                if (currentVol > capacity + 0.5f)
                {
                    _sawmill.Error($"[OVERFLOW] Volume {currentVol:F1}u exceeds beaker limit {capacity}u! STOPPING.");
                    StopJob();
                    return;
                }

                if (Math.Abs(currentVol - _lastTotalAmountBefore) > 0.1f)
                {
                    _stabilizationTimer += 0.5f;
                    if (_stabilizationTimer < 2.0f) 
                    {
                        _sawmill.Info($"[WAIT] Stabilizing solution... ({_lastTotalAmountBefore:F1}u -> {currentVol:F1}u)");
                        _lastTotalAmountBefore = currentVol;
                        return; 
                    }
                }

                _stabilizationTimer = 0f;
                _targetTempReached = false;
                _lastTotalAmountBefore = currentVol;

                // SMART SKIP: Only for wait and heat phases
                if (nextPhase.Type != PhaseType.Dispense)
                {
                    // If product of this phase is already in the beaker
                    if (nextPhase.TargetProduct != null && currentContents.ContainsKey(nextPhase.TargetProduct) && currentContents[nextPhase.TargetProduct] > 0.1f)
                    {
                        _sawmill.Info($"[SKIP] Phase '{nextPhase.Description}' skipped because {nextPhase.TargetProduct} is already obtained.");
                        _currentPlan.PhaseQueue.Dequeue();
                        return; 
                    }

                    // Check for products from FUTURE phases (global skip for Wait/Heat)
                    foreach (var futurePhase in _currentPlan.PhaseQueue.Skip(1))
                    {
                        if (futurePhase.TargetProduct != null && currentContents.ContainsKey(futurePhase.TargetProduct) && currentContents[futurePhase.TargetProduct] > 0.1f)
                        {
                            _sawmill.Info($"[SKIP] Phase '{nextPhase.Description}' skipped because a product of a future phase was detected: {futurePhase.TargetProduct}");
                            _currentPlan.PhaseQueue.Dequeue();
                            return;
                        }
                    }
                }

                if (nextPhase.Type == PhaseType.Dispense)
                {
                    if (_dispenseQueue.Count == 0)
                    {
                        float totalToDispense = nextPhase.Ingredients.Values.Sum(v => v.Float());

                        if (currentVol + totalToDispense > capacity + 0.1f)
                        {
                            float available = capacity - currentVol;
                            float scale = available / totalToDispense;
                            foreach (var (rId, amt) in nextPhase.Ingredients)
                                _dispenseQueue.Enqueue((rId, (int)Math.Floor(amt.Float() * Math.Max(0, scale))));
                        }
                        else
                        {
                            foreach (var (rId, amt) in nextPhase.Ingredients)
                                _dispenseQueue.Enqueue((rId, (int)Math.Round(amt.Float())));
                        }
                    }

                    if (_dispenseQueue.Count == 0 && nextPhase.Ingredients.Count > 0)
                    {
                        _sawmill.Error("[ABORT] No space!");
                        _currentPlan.PhaseQueue.Dequeue();
                        return;
                    }

                    CurrentState = AutoChemState.WaitDispenser;
                    _sawmill.Info($"[START] {nextPhase.Description}");
                    _targetProduct = nextPhase.TargetProduct;
                }
                else if (nextPhase.Type == PhaseType.Wait)
                {
                    CurrentState = AutoChemState.WaitReaction;
                    _sawmill.Info($"[START] {nextPhase.Description}");
                }
                else if (nextPhase.Type == PhaseType.Heat)
                {
                    CurrentState = AutoChemState.WaitHeater;
                    _sawmill.Info($"[START] {nextPhase.Description}");
                }
                break;

            case AutoChemState.WaitDispenser:
                if (_currentPlan == null || _dispenseQueue.Count == 0) { CurrentState = AutoChemState.CheckNextPhase; return; }
                var (firstR, firstAmt) = _dispenseQueue.Peek();
                _dispenser = _scanner.FindMachineWithReagent(firstR);
                
                if (_dispenser == null) 
                { 
                    _sawmill.Warning($"[WAIT] Reagent {firstR} not found! Please add {firstAmt}u manually.");
                    _lastTotalAmountBefore = _scanner.GetFullBreakdown(_beaker!.Value).Values.Sum();
                    CurrentState = AutoChemState.WaitManual; 
                    return; 
                }

                if (_beaker == null || !_scanner.IsBeakerInsideMachine(_dispenser.Value, _beaker.Value)) 
                {
                    _stabilizationTimer += 0.5f;
                    if (_stabilizationTimer >= 2.0f)
                    {
                        _sawmill.Info($"[INFO] Insert beaker into {MetaData(_dispenser.Value).EntityName} for {firstR}...");
                        _stabilizationTimer = 0f;
                    }
                    return; 
                }
                _sawmill.Info($"[DEBUG] Dispenser ready. Starting to dispense {firstR}.");
                CurrentState = AutoChemState.Dispensing;
                _stabilizationTimer = 0f;
                break;

            case AutoChemState.Dispensing:
                if (_beaker == null || _dispenser == null || _currentPlan == null) { CurrentState = AutoChemState.CheckNextPhase; return; }
                if (_dispenseQueue.Count > 0)
                {
                    var (reagentId, remaining) = _dispenseQueue.Peek();
                    
                    if (_waitingForDispenseConfirm)
                    {
                        var currentSnap = _scanner.GetFullBreakdown(_beaker.Value);
                        float currentReagentAmt = currentSnap.TryGetValue(_lastReagentAdded!, out var rAmt) ? rAmt : 0f;
                        float currentProductAmt = (_targetProduct != null && currentSnap.TryGetValue(_targetProduct, out var pAmt)) ? pAmt : 0f;
                        float snapTotal = currentSnap.Values.Sum();

                        float reagentIncrease = currentReagentAmt - _lastReagentAmountBefore;
                        float productIncrease = currentProductAmt - _lastProductAmountBefore;
                        float volumeIncrease = snapTotal - _lastTotalAmountBefore;

                        if (reagentIncrease > 0.05f || productIncrease > 0.05f || volumeIncrease > 0.05f)
                        {
                            float delta = reagentIncrease;
                            if (productIncrease > 0.05f)
                            {
                                float yield = 1f;
                                float reactantRatio = 1f;
                                if (_targetProduct != null)
                                {
                                    var recipe = RobustaChemDatabase.GetRecipe(_targetProduct);
                                    if (recipe != null)
                                    {
                                        if (recipe.Products.TryGetValue(_targetProduct, out var yAmt)) yield = yAmt.Float();
                                        if (recipe.Reactants.TryGetValue(_lastReagentAdded!, out var rData)) reactantRatio = rData.Amount.Float();
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

                            _sawmill.Info($"[VERIFY] {_lastReagentAdded}: dispensed {pouredInt}u (measured {delta:F1}u).");
                            _waitingForDispenseConfirm = false;
                            
                            var list = _dispenseQueue.ToList();
                            var head = list[0];
                            int newRemaining = head.Amount - pouredInt;
                            list.RemoveAt(0);
                            if (newRemaining > 0) list.Insert(0, (reagentId, newRemaining));
                            _dispenseQueue = new Queue<(string, int)>(list);
                            
                            LogBeakerContents();
                            return; 
                        }
                        return; 
                    }

                    var jugLoc = _scanner.FindJugLocation(_dispenser.Value, reagentId);
                    if (jugLoc == null) { _dispenser = null; CurrentState = AutoChemState.WaitDispenser; return; }

                    ReagentDispenserDispenseAmount dose;
                    if (remaining >= 100) dose = ReagentDispenserDispenseAmount.U100;
                    else if (remaining >= 50) dose = ReagentDispenserDispenseAmount.U50;
                    else if (remaining >= 30) dose = ReagentDispenserDispenseAmount.U30;
                    else if (remaining >= 25) dose = ReagentDispenserDispenseAmount.U25;
                    else if (remaining >= 20) dose = ReagentDispenserDispenseAmount.U20;
                    else if (remaining >= 15) dose = ReagentDispenserDispenseAmount.U15;
                    else if (remaining >= 10) dose = ReagentDispenserDispenseAmount.U10;
                    else if (remaining >= 5) dose = ReagentDispenserDispenseAmount.U5;
                    else dose = ReagentDispenserDispenseAmount.U1;

                    if (_lastActiveDose != dose)
                    {
                        _sawmill.Info($"[SYNC] Setting dose {dose}...");
                        _networkHands.SendBuiMessage(_dispenser.Value, new ReagentDispenserSetDispenseAmountMessage(dose));
                        _lastActiveDose = dose;
                        return;
                    }

                    var snapBefore = _scanner.GetFullBreakdown(_beaker.Value);
                    _lastReagentAdded = reagentId;
                    _lastRequestedDose = (int)dose; 
                    _lastReagentAmountBefore = snapBefore.TryGetValue(reagentId, out var rOld) ? rOld : 0f;
                    _lastTotalAmountBefore = snapBefore.Values.Sum();
                    _lastProductAmountBefore = (_targetProduct != null && snapBefore.TryGetValue(_targetProduct, out var pOld)) ? pOld : 0f;
                    _waitingForDispenseConfirm = true;

                    _sawmill.Info($"[ACTION] Dispensing {reagentId} (attempting {dose}u). Remaining in plan: {remaining}u");
                    _networkHands.SendBuiMessage(_dispenser.Value, new ReagentDispenserDispenseReagentMessage(jugLoc.Value));
                }
                else
                {
                    _lastActiveDose = null; 
                    var currentP = _currentPlan.PhaseQueue.Peek();
                    
                    bool nextIsHeat = _currentPlan.PhaseQueue.Count > 1 && _currentPlan.PhaseQueue.ElementAt(1).Type == PhaseType.Heat;
                    if (nextIsHeat || _currentPlan.PhaseQueue.Count == 1)
                        _networkHands.EjectViaUI(_dispenser.Value);

                    _currentPlan.PhaseQueue.Dequeue(); 
                    CurrentState = AutoChemState.CheckNextPhase; 
                }
                break;

            case AutoChemState.WaitManual:
                if (_beaker == null || _currentPlan == null) return;
                if (_dispenseQueue.Count == 0) 
                { 
                    _currentPlan.PhaseQueue.Dequeue(); 
                    CurrentState = AutoChemState.CheckNextPhase; 
                    return; 
                }
                
                var (mR, mN) = _dispenseQueue.Peek();
                var curV = _scanner.GetFullBreakdown(_beaker.Value).Values.Sum();
                float added = curV - _lastTotalAmountBefore;

                if (_scanner.GetReagentAmount(_beaker.Value, mR) >= mN || added >= (mN - 0.1f)) 
                { 
                    _sawmill.Info($"[VERIFY] Manual dispensing of {mR} confirmed.");
                    _dispenseQueue.Dequeue(); 
                    _lastTotalAmountBefore = curV; 
                    
                    if (_dispenseQueue.Count == 0)
                    {
                        _currentPlan.PhaseQueue.Dequeue(); 
                        CurrentState = AutoChemState.CheckNextPhase;
                    }
                    else
                    {
                        CurrentState = AutoChemState.WaitDispenser; 
                    }
                }
                break;

            case AutoChemState.WaitReaction:
                if (_beaker == null || _currentPlan == null || _currentPlan.PhaseQueue.Count == 0) return;
                var wPhase = _currentPlan.PhaseQueue.Peek();
                var contents = _scanner.GetFullBreakdown(_beaker.Value);
                
                bool allDone = true;
                foreach (var r in wPhase.WaitReagents)
                {
                    // SMART CHECK: If the reagent itself is missing, check if its product has appeared
                    if (!contents.ContainsKey(r) || contents[r] < 0.1f)
                    {
                        // Check all FUTURE phases for their results (target products)
                        bool foundInFuture = false;
                        foreach (var futurePhase in _currentPlan.PhaseQueue.Skip(1))
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
                    _sawmill.Info($"[SUCCESS] Reagents obtained or already processed.");
                    _currentPlan.PhaseQueue.Dequeue();
                    CurrentState = AutoChemState.CheckNextPhase;
                    _stabilizationTimer = 0f;
                    _lastTotalAmountBefore = contents.Values.Sum();
                }
                else
                {
                    _stabilizationTimer += 0.5f;
                    if (_stabilizationTimer >= 2.0f)
                    {
                        _sawmill.Info($"[WAIT] Waiting for reaction {string.Join(", ", wPhase.WaitReagents)}... (Volume: {contents.Values.Sum():F1}u)");
                        _stabilizationTimer = 0f;
                    }
                }
                break;

            case AutoChemState.WaitHeater:
                if (_beaker == null) return;
                var heater = _scanner.FindMachine("heater");
                if (heater != null && _scanner.IsBeakerInsideMachine(heater.Value, _beaker.Value)) 
                {
                    CurrentState = AutoChemState.Heating;
                }
                break;

            case AutoChemState.Heating:
                if (_beaker == null || _currentPlan == null || _currentPlan.PhaseQueue.Count == 0) return;
                var hPhase = _currentPlan.PhaseQueue.Peek();
                var t = _scanner.GetBeakerTemperature(_beaker.Value) ?? _lastNetworkTemp;
                
                if (t >= hPhase.TargetTemperature)
                {
                    if (!_targetTempReached)
                    {
                        _sawmill.Info($">>> {hPhase.TargetTemperature}K REACHED! REMOVE BEAKER! <<<");
                        _targetTempReached = true;
                    }
                }

                var heaterNow = _scanner.FindMachine("heater");
                if (heaterNow == null || !_scanner.IsBeakerInsideMachine(heaterNow.Value, _beaker.Value))
                {
                    _sawmill.Info("[DEBUG] Beaker removed from heater.");
                    if (_targetTempReached)
                    {
                        _currentPlan.PhaseQueue.Dequeue();
                        CurrentState = AutoChemState.CheckNextPhase;
                    }
                    else
                    {
                        _sawmill.Warning("[WARN] Beaker removed prematurely! Returning to heater wait.");
                        CurrentState = AutoChemState.WaitHeater;
                    }
                }
                break;

            case AutoChemState.Finish:
                if (_examineSyncTimer > 1.0f)
                {
                    _examineSyncTimer = 0f;
                    LogBeakerContents();
                    _sawmill.Info("[AutoChem] Done!");
                    CurrentState = AutoChemState.Idle;
                }
                break;
        }
    }

    private void LogBeakerContents()
    {
        if (_beaker == null) return;
        var contents = _scanner.GetFullBreakdown(_beaker.Value);
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
        if (_currentPlan == null) return "No Plan";
        switch (CurrentState)
        {
            case AutoChemState.CheckNextPhase: return "Stabilizing...";
            case AutoChemState.WaitDispenser: return "Looking for dispenser...";
            case AutoChemState.Dispensing: return _dispenseQueue.Count > 0 ? $"Adding {_dispenseQueue.Peek().Reagent}" : "Complete";
            case AutoChemState.WaitManual:
                return _dispenseQueue.Count > 0 ? $"MANUAL: {_dispenseQueue.Peek().Reagent} ({_dispenseQueue.Peek().Amount}u)" : "Manual done";
            case AutoChemState.WaitReaction:
                if (_currentPlan.PhaseQueue.Count == 0) return "Reaction done";
                return $"Reacting: {string.Join(", ", _currentPlan.PhaseQueue.Peek().WaitReagents)}";
            case AutoChemState.WaitHeater: return "Wait for Heater...";
            case AutoChemState.Heating:
                if (_currentPlan.PhaseQueue.Count == 0) return "Heating done";
                var hPhase = _currentPlan.PhaseQueue.Peek();
                var curT = (_beaker != null) ? (_scanner.GetBeakerTemperature(_beaker.Value) ?? _lastNetworkTemp ?? 0) : 0;
                return _targetTempReached ? "DONE! Eject beaker!" : $"Heating: {curT:F1}K / {hPhase.TargetTemperature}K";
            case AutoChemState.Finish: return "ALL DONE! Take beaker.";
            default: return CurrentState.ToString();
        }
    }

    public float GetProgress()
    {
        if (_currentPlan == null || _currentPlan.TotalPhases == 0) return 0f;
        int completed = _currentPlan.TotalPhases - _currentPlan.PhaseQueue.Count;
        float baseProgress = (float)completed / _currentPlan.TotalPhases;
        float phaseWeight = 1f / _currentPlan.TotalPhases;

        if (CurrentState == AutoChemState.Heating && _currentPlan.PhaseQueue.Count > 0)
        {
            var phase = _currentPlan.PhaseQueue.Peek();
            var curT = (_beaker != null) ? (_scanner.GetBeakerTemperature(_beaker.Value) ?? _lastNetworkTemp ?? 293.15f) : 293.15f;
            float tProgress = Math.Clamp((curT - 293.15f) / (phase.TargetTemperature - 293.15f), 0f, 1f);
            return baseProgress + (tProgress * phaseWeight);
        }

        return baseProgress;
    }
}