using System.Collections.Generic;
using FluentAssertions;
using RobustoClient.Systems.AutoChem;
using Xunit;

namespace RobustoClient.Tests.Systems.AutoChem;

public class RobustaRecipeSolverTests
{
    [Fact]
    public void BaseDispenserReagent_CreatesSingleDispensePhase_WithoutWait()
    {
        RobustaChemDatabase.LoadRecipes([]);

        var plan = RobustaRecipeSolver.CreatePlan("carbon", amount: 25f, capacity: 50f);

        plan.Should().NotBeNull();
        plan!.TotalPhases.Should().Be(1);
        var phase = plan.PhaseQueue.Should().ContainSingle().Subject;
        phase.Type.Should().Be(PhaseType.Dispense);
        phase.Ingredients.Should().ContainKey("carbon").WhoseValue.Should().Be(25f);
    }

    [Fact]
    public void SingleStageRecipe_CreatesDispenseAndWaitPhases()
    {
        RobustaChemDatabase.LoadRecipes(
        [
            new ChemRecipeDefinition(
                Id: "TableSaltReaction",
                Products: new Dictionary<string, float> { ["TableSalt"] = 2f },
                Reactants:
                [
                    new ChemReactant("sodium", 1f),
                    new ChemReactant("chlorine", 1f)
                ])
        ]);

        var plan = RobustaRecipeSolver.CreatePlan("TableSalt", amount: 20f, capacity: 50f);

        plan.Should().NotBeNull();
        plan!.TargetReagent.Should().Be("TableSalt");
        plan.TotalPhases.Should().Be(2);

        var phases = plan.PhaseQueue.ToArray();
        phases[0].Type.Should().Be(PhaseType.Dispense);
        phases[0].TargetProduct.Should().Be("TableSalt");
        phases[0].Ingredients.Should().ContainKey("sodium").WhoseValue.Should().Be(10f);
        phases[0].Ingredients.Should().ContainKey("chlorine").WhoseValue.Should().Be(10f);

        phases[1].Type.Should().Be(PhaseType.Wait);
        phases[1].WaitReagents.Should().ContainSingle().Which.Should().Be("TableSalt");
    }

    [Fact]
    public void MultiStageRecipe_SynthesizesIntermediateAndHeatsBeforeFinalWait()
    {
        RobustaChemDatabase.LoadRecipes(
        [
            new ChemRecipeDefinition(
                Id: "AmmoniaReaction",
                Products: new Dictionary<string, float> { ["Ammonia"] = 3f },
                Reactants:
                [
                    new ChemReactant("hydrogen", 3f),
                    new ChemReactant("nitrogen", 1f)
                ]),
            new ChemRecipeDefinition(
                Id: "DiethylamineReaction",
                Products: new Dictionary<string, float> { ["Diethylamine"] = 2f },
                Reactants:
                [
                    new ChemReactant("Ammonia", 1f),
                    new ChemReactant("ethanol", 1f)
                ],
                MinimumTemperature: 370f)
        ]);

        var plan = RobustaRecipeSolver.CreatePlan("Diethylamine", amount: 20f, capacity: 50f);

        plan.Should().NotBeNull();
        var phases = plan!.PhaseQueue.ToArray();
        phases.Should().HaveCount(5);

        // Stage 1: Ammonia (10u needed -> ceil(10 / 3) = 4 cycles -> 12u hydrogen, 4u nitrogen)
        phases[0].Type.Should().Be(PhaseType.Dispense);
        phases[0].TargetProduct.Should().Be("Ammonia");
        phases[0].Ingredients["hydrogen"].Should().Be(12f);
        phases[0].Ingredients["nitrogen"].Should().Be(4f);

        phases[1].Type.Should().Be(PhaseType.Wait);
        phases[1].WaitReagents.Should().ContainSingle().Which.Should().Be("Ammonia");

        // Stage 2: Diethylamine (10 cycles -> 10u ethanol + Heat 370K + Wait)
        phases[2].Type.Should().Be(PhaseType.Dispense);
        phases[2].TargetProduct.Should().Be("Diethylamine");
        phases[2].Ingredients.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, float>("ethanol", 10f));

        phases[3].Type.Should().Be(PhaseType.Heat);
        phases[3].TargetTemperature.Should().Be(370f);
        phases[3].TargetProduct.Should().Be("Diethylamine");

        phases[4].Type.Should().Be(PhaseType.Wait);
        phases[4].WaitReagents.Should().ContainSingle().Which.Should().Be("Diethylamine");
    }

    [Fact]
    public void CatalystDeltaTracking_AcrossMultipleStages_PreventsDuplicateCatalystAddition()
    {
        RobustaChemDatabase.LoadRecipes(
        [
            new ChemRecipeDefinition(
                Id: "Stage1WithCatalyst",
                Products: new Dictionary<string, float> { ["IntermediateA"] = 1f },
                Reactants:
                [
                    new ChemReactant("carbon", 1f),
                    new ChemReactant("plasma", 5f, Catalyst: true)
                ]),
            new ChemRecipeDefinition(
                Id: "Stage2WithSameCatalyst",
                Products: new Dictionary<string, float> { ["IntermediateB"] = 1f },
                Reactants:
                [
                    new ChemReactant("IntermediateA", 1f),
                    new ChemReactant("oxygen", 1f),
                    new ChemReactant("plasma", 5f, Catalyst: true)
                ]),
            new ChemRecipeDefinition(
                Id: "Stage3WithHigherCatalyst",
                Products: new Dictionary<string, float> { ["FinalCompound"] = 1f },
                Reactants:
                [
                    new ChemReactant("IntermediateB", 1f),
                    new ChemReactant("hydrogen", 1f),
                    new ChemReactant("plasma", 8f, Catalyst: true)
                ])
        ]);

        var plan = RobustaRecipeSolver.CreatePlan("FinalCompound", amount: 10f, capacity: 50f);

        plan.Should().NotBeNull();
        var dispensePhases = plan!.PhaseQueue.Where(p => p.Type == PhaseType.Dispense).ToArray();
        dispensePhases.Should().HaveCount(3);

        dispensePhases[0].TargetProduct.Should().Be("IntermediateA");
        dispensePhases[0].Ingredients["carbon"].Should().Be(10f);
        dispensePhases[0].Ingredients["plasma"].Should().Be(5f);

        // Stage 2 needs 5u plasma catalyst which is already present (delta = 0)
        dispensePhases[1].TargetProduct.Should().Be("IntermediateB");
        dispensePhases[1].Ingredients["oxygen"].Should().Be(10f);
        dispensePhases[1].Ingredients.Should().NotContainKey("plasma");

        // Stage 3 needs 8u plasma catalyst, so only the +3u delta is dispensed
        dispensePhases[2].TargetProduct.Should().Be("FinalCompound");
        dispensePhases[2].Ingredients["hydrogen"].Should().Be(10f);
        dispensePhases[2].Ingredients["plasma"].Should().Be(3f);
    }

    [Fact]
    public void LCMAndYieldSkew_ClampsToSafeCapacityAndScalesFractionalReactants()
    {
        RobustaChemDatabase.LoadRecipes(
        [
            new ChemRecipeDefinition(
                Id: "InaprovalineReaction",
                Products: new Dictionary<string, float> { ["Inaprovaline"] = 3f },
                Reactants:
                [
                    new ChemReactant("oxygen", 1f),
                    new ChemReactant("carbon", 1f),
                    new ChemReactant("sugar", 1f)
                ]),
            new ChemRecipeDefinition(
                Id: "FractionalReaction",
                Products: new Dictionary<string, float> { ["FractionalChem"] = 1f },
                Reactants:
                [
                    new ChemReactant("silicon", 0.5f),
                    new ChemReactant("potassium", 0.5f)
                ])
        ]);

        // Yield Skew safety limit: requesting full 50u capacity clamps target to 47.5u (16 cycles = 48u total volume, not 17 cycles = 51u overflow)
        var skewPlan = RobustaRecipeSolver.CreatePlan("Inaprovaline", amount: 50f, capacity: 50f);
        skewPlan.Should().NotBeNull();
        var skewDispense = skewPlan!.PhaseQueue.First(p => p.Type == PhaseType.Dispense);
        skewDispense.Ingredients["oxygen"].Should().Be(16f);
        skewDispense.Ingredients["carbon"].Should().Be(16f);
        skewDispense.Ingredients["sugar"].Should().Be(16f);

        // Fractional reactant (0.5u) forces LCM = 10 so dispensed amounts are whole integers (5u + 5u)
        var lcmPlan = RobustaRecipeSolver.CreatePlan("FractionalChem", amount: 3f, capacity: 50f);
        lcmPlan.Should().NotBeNull();
        var lcmDispense = lcmPlan!.PhaseQueue.First(p => p.Type == PhaseType.Dispense);
        lcmDispense.Ingredients["silicon"].Should().Be(5f);
        lcmDispense.Ingredients["potassium"].Should().Be(5f);

        RobustaRecipeSolver.CalculateBestBatch("Inaprovaline", capacity: 100f).Should().Be(99f);
    }

    [Fact]
    public void CircularReactionDefinitions_FailGracefully_WithoutInfiniteRecursion()
    {
        RobustaChemDatabase.LoadRecipes(
        [
            new ChemRecipeDefinition(
                Id: "CycleA",
                Products: new Dictionary<string, float> { ["ReagentA"] = 1f },
                Reactants: [new ChemReactant("ReagentB", 1f)]),
            new ChemRecipeDefinition(
                Id: "CycleB",
                Products: new Dictionary<string, float> { ["ReagentB"] = 1f },
                Reactants: [new ChemReactant("ReagentA", 1f)])
        ]);

        var plan = RobustaRecipeSolver.CreatePlan("ReagentA", amount: 10f, capacity: 50f);

        plan.Should().BeNull();
    }
}
