using System.Numerics;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using RobustoClient.ImGui;
using RobustoClient.Systems.AutoChem;

namespace RobustoClient.UI.ImGui;

public sealed class AutoChemImGui
{
    /// <summary>Window title plus the id the layout store files this window under.</summary>
    private const string WindowTitle = "Robusta Auto-Chemist##" + WindowLayoutId.AutoChem;

    public bool IsOpen;

    private string _searchQuery = "";
    private string _selectedChem = "";
    private int _targetAmount = 30;
    private string _statusMessage = "Idle — select a chemical to begin.";
    private readonly HashSet<string> _expandedRecipes = new(StringComparer.OrdinalIgnoreCase);

    public void Draw()
    {
        if (WindowLayout.Get(WindowLayoutId.AutoChem) is { } placement)
        {
            SetNextWindowPos(WindowLayout.ClampPosition(placement, GetIO().DisplaySize), ImGuiCond.FirstUseEver);
            SetNextWindowSize(new Vector2(placement.Width, placement.Height), ImGuiCond.FirstUseEver);
        }
        else
        {
            SetNextWindowSize(new Vector2(440f, 520f), ImGuiCond.FirstUseEver);
        }

        SetNextWindowSizeConstraints(new Vector2(360f, 340f), new Vector2(900f, 950f));

        if (!Begin(WindowTitle, ref IsOpen, ImGuiWindowFlags.None))
        {
            End();
            return;
        }

        WindowLayout.Remember(WindowLayoutId.AutoChem, GetWindowPos(), GetWindowSize());

        if (IsMouseReleased(ImGuiMouseButton.Left))
            WindowLayout.FlushIfPending();

        if (!RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan))
        {
            End();
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

        TextColored(
            RobustaImGuiTheme.AccentCyan,
            string.IsNullOrEmpty(_selectedChem) ? "Selected: None" : $"Selected: {_selectedChem}");

        float headerRightEdge = GetCursorPosX() + GetContentRegionAvail().X;
        float inputWidth = Math.Clamp(GetContentRegionAvail().X - 220f, 90f, 150f);
        SetNextItemWidth(inputWidth);
        if (InputInt("Target Volume (u)", ref _targetAmount, 5, 10))
            _targetAmount = Math.Clamp(_targetAmount, 1, 300);

        SameLine(headerRightEdge - 96f);
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
            ProgressBar(
                Math.Clamp(autoChem.GetProgress(), 0f, 1f),
                new Vector2(-1f, 6f),
                "");
        }

        TextColored(
            isRunning ? RobustaImGuiTheme.AccentGreen : RobustaImGuiTheme.TextMuted,
            _statusMessage);

        RobustaImGuiWidgets.EndSectionCard();

        Spacing();
        SetNextItemWidth(-1f);
        InputTextWithHint("##ChemSearch", "Search chemical recipe...", ref _searchQuery, 128);
        Spacing();

        // Scrollable Recipe List
        BeginChild("##ChemRecipeList", Vector2.Zero, ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);

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

            PushID(chemId);

            float rightEdge = GetCursorPosX() + GetContentRegionAvail().X;
            AlignTextToFramePadding();
            if (isSelected)
                TextColored(RobustaImGuiTheme.AccentCyan, chemId);
            else
                TextUnformatted(chemId);

            SameLine(rightEdge - 134f);
            if (RobustaImGuiWidgets.AccentButton(isExpanded ? "Hide" : "Recipe", new Vector2(62f, 24f), isExpanded))
            {
                if (!isExpanded) _expandedRecipes.Add(chemId);
                else _expandedRecipes.Remove(chemId);
            }

            SameLine(rightEdge - 66f);
            if (RobustaImGuiWidgets.AccentButton("Select", new Vector2(66f, 24f), isSelected))
            {
                _selectedChem = chemId;
                _statusMessage = $"Selected: {chemId}";
            }

            if (isExpanded)
            {
                Indent(14f);
                foreach (var (reactantId, amount) in RobustaChemDatabase.GetReactantsAndAmounts(recipe))
                {
                    RobustaImGuiWidgets.MutedText($"• {reactantId}: {amount}u");
                }
                if (RobustaChemDatabase.RequiresHeating(recipe))
                {
                    TextColored(RobustaImGuiTheme.AccentGold, "• Requires Heating");
                }
                Unindent(14f);
            }

            Separator();
            PopID();
        }

        EndChild();
        End();
    }
}
