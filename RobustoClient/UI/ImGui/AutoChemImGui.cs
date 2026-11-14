using System.Numerics;
using Hexa.NET.ImGui;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using RobustoClient.ImGui;
using RobustoClient.Systems.AutoChem;

namespace RobustoClient.UI.ImGui;

public sealed class AutoChemImGui
{
    public bool IsOpen;

    private string _searchQuery = "";
    private string _selectedChem = "";
    private int _targetAmount = 30;
    private string _statusMessage = "Idle — select a chemical to begin.";
    private readonly HashSet<string> _expandedRecipes = new(StringComparer.OrdinalIgnoreCase);

    public void Draw()
    {
        Hexa.NET.ImGui.ImGui.SetNextWindowSize(new Vector2(440f, 520f), ImGuiCond.FirstUseEver);
        Hexa.NET.ImGui.ImGui.SetNextWindowSizeConstraints(new Vector2(360f, 340f), new Vector2(900f, 950f));

        if (!Hexa.NET.ImGui.ImGui.Begin("Robusta Auto-Chemist##AutoChemWin", ref IsOpen, ImGuiWindowFlags.NoCollapse))
        {
            Hexa.NET.ImGui.ImGui.End();
            return;
        }

        if (!RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan))
        {
            Hexa.NET.ImGui.ImGui.End();
            return;
        }

        if (!RobustaChemDatabase.IsInitialized)
            RobustaChemDatabase.Initialize();

        var autoChem = entMan.System<RobustaAutoChemSystem>();
        bool isRunning = autoChem.CurrentState != IdleState.Instance;

        if (isRunning)
        {
            _statusMessage = autoChem.GetStatusInfo();
        }

        // Control Header Card
        RobustaImGuiWidgets.BeginSectionCard("##AutoChemHeader", "SYNTHESIS CONTROL");

        Hexa.NET.ImGui.ImGui.TextColored(
            RobustaImGuiTheme.AccentCyan,
            string.IsNullOrEmpty(_selectedChem) ? "Selected: None" : $"Selected: {_selectedChem}");

        float headerRightEdge = Hexa.NET.ImGui.ImGui.GetCursorPosX() + Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X;
        float inputWidth = Math.Clamp(Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 220f, 90f, 150f);
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(inputWidth);
        if (Hexa.NET.ImGui.ImGui.InputInt("Target Volume (u)", ref _targetAmount, 5, 10))
            _targetAmount = Math.Clamp(_targetAmount, 1, 300);

        Hexa.NET.ImGui.ImGui.SameLine(headerRightEdge - 96f);
        if (isRunning)
        {
            if (RobustaImGuiWidgets.DangerButton("Stop Job", new Vector2(96f, 0f)))
            {
                autoChem.StopJob();
                _statusMessage = "Job stopped.";
            }
        }
        else
        {
            if (RobustaImGuiWidgets.AccentButton("Start Brew", new Vector2(96f, 0f), true))
            {
                if (string.IsNullOrWhiteSpace(_selectedChem))
                {
                    _statusMessage = "Select a chemical first!";
                }
                else
                {
                    autoChem.StartJob(_selectedChem, _targetAmount);
                    _statusMessage = $"Brewing {_selectedChem} ({_targetAmount}u)...";
                }
            }
        }

        if (isRunning)
        {
            Hexa.NET.ImGui.ImGui.ProgressBar(
                Math.Clamp(autoChem.GetProgress(), 0f, 1f),
                new Vector2(-1f, 6f),
                "");
        }

        Hexa.NET.ImGui.ImGui.TextColored(
            isRunning ? RobustaImGuiTheme.AccentGreen : RobustaImGuiTheme.TextMuted,
            _statusMessage);

        RobustaImGuiWidgets.EndSectionCard();

        Hexa.NET.ImGui.ImGui.Spacing();
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1f);
        Hexa.NET.ImGui.ImGui.InputTextWithHint("##ChemSearch", "Search chemical recipe...", ref _searchQuery, 128);
        Hexa.NET.ImGui.ImGui.Spacing();

        // Scrollable Recipe List
        Hexa.NET.ImGui.ImGui.BeginChild("##ChemRecipeList", Vector2.Zero, ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);

        IEnumerable<string> chems = RobustaChemDatabase.RecipesByProduct.Keys;
        if (!string.IsNullOrWhiteSpace(_searchQuery))
            chems = chems.Where(k => k.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));

        foreach (var chemId in chems.OrderBy(k => k))
        {
            var recipe = RobustaChemDatabase.GetRecipe(chemId);
            if (recipe == null)
                continue;

            bool isSelected = chemId.Equals(_selectedChem, StringComparison.OrdinalIgnoreCase);
            bool isExpanded = _expandedRecipes.Contains(chemId);

            Hexa.NET.ImGui.ImGui.PushID(chemId);

            float rightEdge = Hexa.NET.ImGui.ImGui.GetCursorPosX() + Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X;
            Hexa.NET.ImGui.ImGui.AlignTextToFramePadding();
            if (isSelected)
                Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.AccentCyan, chemId);
            else
                Hexa.NET.ImGui.ImGui.TextUnformatted(chemId);

            Hexa.NET.ImGui.ImGui.SameLine(rightEdge - 134f);
            if (RobustaImGuiWidgets.AccentButton(isExpanded ? "Hide" : "Recipe", new Vector2(62f, 24f), isExpanded))
            {
                if (!isExpanded) _expandedRecipes.Add(chemId);
                else _expandedRecipes.Remove(chemId);
            }

            Hexa.NET.ImGui.ImGui.SameLine(rightEdge - 66f);
            if (RobustaImGuiWidgets.AccentButton("Select", new Vector2(66f, 24f), isSelected))
            {
                _selectedChem = chemId;
                _statusMessage = $"Selected: {chemId}";
            }

            if (isExpanded)
            {
                Hexa.NET.ImGui.ImGui.Indent(14f);
                foreach (var (reactantId, amount) in RobustaChemDatabase.GetReactantsAndAmounts(recipe))
                {
                    Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.TextMuted, $"• {reactantId}: {amount}u");
                }
                if (RobustaChemDatabase.RequiresHeating(recipe))
                {
                    Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.AccentGold, "• Requires Heating");
                }
                Hexa.NET.ImGui.ImGui.Unindent(14f);
            }

            Hexa.NET.ImGui.ImGui.Separator();
            Hexa.NET.ImGui.ImGui.PopID();
        }

        Hexa.NET.ImGui.ImGui.EndChild();
        Hexa.NET.ImGui.ImGui.End();
    }
}
