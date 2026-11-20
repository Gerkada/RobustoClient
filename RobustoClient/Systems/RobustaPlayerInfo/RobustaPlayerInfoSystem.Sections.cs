using System.Globalization;
using Content.Shared.Security;
using RobustoClient.Systems.PlayerState;

namespace RobustoClient.Systems;

/// <summary>Section builders for <see cref="RobustaPlayerInfoSystem.Compose"/>.</summary>
public sealed partial class RobustaPlayerInfoSystem
{
    #region Display tables

    /// <summary>Actions the DNA store can sell, mapped to the name the panel shows.</summary>
    private static readonly Dictionary<string, string> _changelingPurchases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ActionRetractableItemArmblade"] = "Armblade",
        ["ActionChangelingBiodegrade"] = "Biodegrade",
        ["ActionChangelingStingDna"] = "Extract DNA",
        ["ActionChangelingStingBlind"] = "Blind Sting",
        ["ActionChangelingStingCryogenic"] = "Cryogenic Sting",
        ["ActionChangelingStingLead"] = "Lethal Sting",
        ["ActionChangelingStingMute"] = "Mute Sting",
        ["ActionChangelingStingArmblade"] = "Armblade Sting",
        ["ActionChangelingStingHemotoxin"] = "Hemotoxin Sting",
        ["ActionChangelingLastResort"] = "Last Resort",
        ["ActionChangelingVoiceMimic"] = "Voice Mimicry",
        ["ActionChangelingNightVision"] = "Night Vision",
        ["ActionChangelingFakeMindshieldToggle"] = "Fake Mindshield",
        ["ActionChangelingFleshtomb"] = "Fleshtomb"
    };

    /// <summary>The one purchase that grants a component instead of an action.</summary>
    private const string FleshClothingName = "Flesh Clothing";

    #endregion

    #region Helpers

    /// <summary>Compact countdown: <c>2m 14s</c>, or <c>45s</c> under a minute.</summary>
    internal static string FormatDuration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
            return "0s";

        return span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
            : $"{span.Seconds}s";
    }

    /// <summary>Spaced label for a camelCase id: <c>HasNoSlips</c> reads as <c>No Slips</c>.</summary>
    internal static string FlagLabel(string flagId)
    {
        var text = flagId.StartsWith("Has", StringComparison.Ordinal)
            ? flagId[3..]
            : flagId.StartsWith("Is", StringComparison.Ordinal)
                ? flagId[2..]
                : flagId;

        var builder = new System.Text.StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsUpper(text[i]) && !char.IsUpper(text[i - 1]))
                builder.Append(' ');

            builder.Append(text[i]);
        }

        return builder.ToString();
    }

    private static InfoRow ChildRow(string label, IReadOnlyList<string> values) =>
        new(label, Count(values.Count), StackedRows(values));

    /// <summary>Collapses repeated names into one row with a count.</summary>
    private static List<InfoRow> StackedRows(IEnumerable<string> names) =>
        names
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new InfoRow(g.Key, g.Count() > 1 ? "x" + Count(g.Count()) : string.Empty))
            .ToList();

    private static InfoRow WeaponRow(string label, IReadOnlyList<WeaponEntry> entries) =>
        new(label, Count(entries.Count), entries.Select(ToWeaponRow).ToList());

    private static InfoRow ToWeaponRow(WeaponEntry entry)
    {
        var parts = new List<string>();
        if (entry.Damage > 0f)
            parts.Add(entry.Damage.ToString("0.#", CultureInfo.InvariantCulture));

        var ammunition = DescribeAmmunition(entry.Ammunition);
        if (!string.IsNullOrEmpty(ammunition))
            parts.Add(ammunition);

        return new InfoRow(entry.Name, string.Join(", ", parts));
    }

    private static string DescribeAmmunition(AmmunitionState ammunition)
    {
        if (ammunition.HitsLeft is { } hits)
            return hits + " hits";

        if (ammunition.Shots is { } shots && ammunition.Capacity is { } capacity)
            return $"{shots}/{capacity}";

        return string.Empty;
    }

    /// <summary>Folds repeated occurrences of one reagent into a single entry.</summary>
    private static ChemicalEntry MergeReagent(IEnumerable<ChemicalEntry> entries)
    {
        var merged = default(ChemicalEntry);
        var first = true;

        foreach (var entry in entries)
        {
            if (first)
            {
                merged = entry;
                first = false;
                continue;
            }

            merged = merged! with
            {
                Amount = merged.Amount + entry.Amount,
                Severity = merged.Severity.Length >= entry.Severity.Length ? merged.Severity : entry.Severity
            };
        }

        return merged!;
    }

    /// <summary>Amount first, then the contraband prefix when the reagent has one.</summary>
    private static string DescribeChemical(ChemicalEntry entry) =>
        ContraPrefix(entry.Severity) +
        entry.Amount.ToString("0.#", CultureInfo.InvariantCulture) + "u";

    /// <summary>Contraband prefix for a severity, Major outranks Minor.</summary>
    internal static string ContraPrefix(string severity) => severity switch
    {
        "Major" => "[MAJOR CONTRA] ",
        "Minor" => "[MINOR CONTRA] ",
        _ => string.Empty
    };

    /// <summary>The strongest contraband severity across a group, or empty when none has one.</summary>
    internal static string WorstSeverity(IEnumerable<ChemicalEntry> entries)
    {
        var worst = string.Empty;

        foreach (var entry in entries)
        {
            if (entry.Severity == "Major")
                return "Major";

            if (entry.Severity == "Minor")
                worst = "Minor";
        }

        return worst;
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Names of the store purchases the entity holds, sorted.</summary>
    internal static List<string> NameChangelingPurchases(
        IEnumerable<string> actionPrototypeIds,
        bool fleshClothing = false)
    {
        var names = new List<string>();

        foreach (var id in actionPrototypeIds)
        {
            if (_changelingPurchases.TryGetValue(id, out var name))
                names.Add(name);
        }

        if (fleshClothing)
            names.Add(FleshClothingName);

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    #endregion

    #region Builders

    private static InfoSection BuildGeneral(PlayerSnapshot snapshot)
    {
        var identity = snapshot.Identity;

        var rows = new List<InfoRow> { new("Name", identity?.EntityName ?? "Unknown") };

        if (!string.IsNullOrEmpty(identity?.SessionName))
            rows.Add(new InfoRow("Player", identity!.SessionName));

        rows.Add(new InfoRow("Job", string.IsNullOrEmpty(identity?.JobTitle) ? "Unknown" : identity!.JobTitle));
        rows.Add(new InfoRow("Range", FormatRange(identity?.RangeMeters)));

        if (snapshot.Equipment is { } equipment)
        {
            var armor = CombineArmorGroups(equipment.ArmorCoefficients);
            if (armor.Count > 0)
                rows.Add(new InfoRow("Armor", Count(armor.Count), armor));

            if (equipment.Weapons.Count > 0)
                rows.Add(WeaponRow("Weapons", equipment.Weapons));
        }

        return new InfoSection(InfoSectionKind.General, rows);
    }

    /// <summary>One header for every way the target reads as a threat.</summary>
    private static InfoSection? BuildAntag(PlayerSnapshot snapshot)
    {
        var rows = new List<InfoRow>();

        if (snapshot.Antagonist is { } antagonist)
        {
            rows.Add(new InfoRow(
                "Antagonist",
                antagonist.Kind + (antagonist.Retained ? " (retained)" : string.Empty)));
        }

        if (snapshot.Syndicate is { } syndicate)
        {
            var status = new List<string>();
            if (syndicate.UplinkStatus is { } uplink)
                status.Add(uplink);
            if (syndicate.HasContraband)
                status.Add("CONTRABAND");
            if (syndicate.Emagged)
                status.Add("EMAGGED");

            // A hedged signal with no confirmed status still needs a row: it is what raises the
            // radar's [SYNDICATE?] tag, so the section must not come back empty.
            if (status.Count == 0 && syndicate.HasHedgedSignal)
                status.Add("SUSPECTED");

            if (status.Count > 0)
                rows.Add(new InfoRow("Status", string.Join(", ", status)));

            if (syndicate.Gear.Count > 0)
                rows.Add(ChildRow("Items", syndicate.Gear));

            var implants = syndicate.Implants
                .Where(implant => implant.Category == "Syndicate")
                .Select(implant => implant.Name)
                .ToList();

            if (implants.Count > 0)
                rows.Add(ChildRow("Implants", implants));
        }

        if (snapshot.Antagonist?.Changeling is { } changeling &&
            BuildChangeling(changeling) is { } changelingRow)
        {
            rows.Add(changelingRow);
        }

        if (snapshot.Theft is { } theft)
            rows.Add(BuildTheft(theft));

        return rows.Count == 0 ? null : new InfoSection(InfoSectionKind.Syndicate, rows);
    }

    /// <summary>The changeling dropdown, empty rows are omitted.</summary>
    private static InfoRow? BuildChangeling(ChangelingData changeling)
    {
        var rows = new List<InfoRow>();

        if (changeling.Triggers.Count > 0)
            rows.Add(new InfoRow("Status", string.Join(", ", changeling.Triggers)));

        if (changeling.Dna is { } dna)
            rows.Add(new InfoRow("DNA", Count(dna)));

        if (changeling.HorrorRemaining is { } remaining)
            rows.Add(new InfoRow("Horror form", FormatDuration(remaining) + " left"));

        var purchased = NameChangelingPurchases(changeling.PurchasedActionIds, changeling.HasFleshClothing);
        if (purchased.Count > 0)
            rows.Add(ChildRow("Purchased", purchased));

        return rows.Count == 0 ? null : new InfoRow("Changeling", Count(rows.Count), rows);
    }

    /// <summary>The theft dropdown, grouped by the severity that flagged it.</summary>
    private static InfoRow? BuildTheft(TheftData theft)
    {
        if (theft.Items.Count == 0)
            return null;

        var rows = theft.Items
            .GroupBy(entry => entry.Severity, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new InfoRow(
                FlagLabel(group.Key),
                Count(group.Count()),
                StackedRows(group.Select(entry => entry.Name))))
            .ToList();

        return new InfoRow("Theft", Count(theft.Items.Count), rows);
    }

    /// <summary>Reagents and medical items, with the holder's worst contraband severity.</summary>
    private static InfoSection? BuildChemical(PlayerSnapshot snapshot)
    {
        if (snapshot.Chemistry is not { } chemistry)
            return null;

        var rows = new List<InfoRow>();

        if (chemistry.Contents.Count > 0)
        {
            var containers = chemistry.Contents
                .GroupBy(chemical => chemical.Holder)
                .Select(group => new InfoRow(
                    ContraPrefix(WorstSeverity(group)) + group.Key,
                    string.Empty,
                    group.GroupBy(chemical => chemical.Reagent, StringComparer.OrdinalIgnoreCase)
                        .Select(reagents => new InfoRow(reagents.Key, DescribeChemical(MergeReagent(reagents))))
                        .ToList()))
                .ToList();

            rows.Add(new InfoRow("Contents", Count(chemistry.Contents.Count), containers));
        }

        if (chemistry.Healing.Count > 0)
        {
            rows.Add(new InfoRow(
                "Healing",
                Count(chemistry.Healing.Count),
                chemistry.Healing.Select(healing => new InfoRow(
                    healing.Name,
                    healing.Count > 1 ? "x" + Count(healing.Count) : string.Empty)).ToList()));
        }

        // The flag fired but the rows named nothing, so say so rather than showing an empty section.
        if (rows.Count == 0 && chemistry.Dangerous)
            rows.Add(new InfoRow("Dangerous reagent", "unidentified"));

        return rows.Count == 0 ? null : new InfoSection(InfoSectionKind.Chemical, rows);
    }

    /// <summary>Department membership, criminal status, and the non-lethals security carries.</summary>
    private static InfoSection? BuildSecurity(PlayerSnapshot snapshot)
    {
        if (snapshot.Security is not { } security)
            return null;

        var rows = new List<InfoRow>();

        if (security.Status != SecurityStatus.None)
            rows.Add(new InfoRow("Status", security.Status.ToString()));

        if (snapshot.Equipment?.NonLethals is { Count: > 0 } nonLethals)
            rows.Add(WeaponRow("Non-lethals", nonLethals));

        return rows.Count == 0 ? null : new InfoSection(InfoSectionKind.Security, rows);
    }

    /// <summary>The command dropdown: the mindshield alert first, then the items by role.</summary>
    private static InfoSection? BuildCommand(PlayerSnapshot snapshot)
    {
        if (snapshot.Command is not { } command)
            return null;

        var rows = new List<InfoRow>();

        // Command gear without a mindshield is the shape of a disguise, so it outranks the item list.
        if (!command.Mindshielded)
            rows.Add(new InfoRow("NO MINDSHIELD", string.Empty));

        if (command.Items.Count > 0)
        {
            var byRole = command.Items
                .GroupBy(item => item.Role)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new InfoRow(
                    group.Key,
                    Count(group.Count()),
                    StackedRows(group.Select(item => item.ItemName))))
                .ToList();

            rows.Add(new InfoRow("Command items", Count(command.Items.Count), byRole));
        }

        return rows.Count == 0 ? null : new InfoSection(InfoSectionKind.Command, rows);
    }

    /// <summary>Flags marked <c>Display</c> that fired.</summary>
    private static InfoSection? BuildFlags(PlayerSnapshot snapshot)
    {
        var rows = new List<InfoRow>();

        foreach (var result in snapshot.Flags.Results)
        {
            if (!result.Triggered || !result.Display)
                continue;

            rows.Add(new InfoRow(FlagLabel(result.Flag.Id), string.Empty));
        }

        return rows.Count == 0 ? null : new InfoSection(InfoSectionKind.Flags, rows);
    }

    #endregion
}
