using System.Linq;
using System.Reflection;
using Content.Shared.Security;
using FluentAssertions;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using RobustoClient.Systems;
using RobustoClient.Systems.Abstract;
using RobustoClient.Systems.PlayerState;
using Xunit;

namespace RobustoClient.Tests.Systems;

/// <summary>
/// Covers the Info panel's formatting: section gating, armor rows and the command-item role table.
/// Every case builds a <see cref="PlayerSnapshot"/> by hand, which is the point of the state and
/// presentation split: this file needs no client.
/// </summary>
public class PlayerInfoTests
{
    private static readonly EntityUid Subject = new(1);

    private static PlayerSnapshot Snapshot(
        bool security = false,
        bool command = false,
        bool syndicate = false,
        bool syndicateGear = false,
        bool noMindshield = false,
        PlayerFlag? extraFlag = null,
        IReadOnlyList<string>? implants = null,
        IReadOnlyList<WeaponEntry>? weapons = null,
        IReadOnlyList<WeaponEntry>? nonLethals = null,
        IReadOnlyList<CommandItemEntry>? commandItems = null,
        IReadOnlyDictionary<string, float>? armor = null,
        ChangelingData? changeling = null,
        IReadOnlyList<TheftEntry>? theftItems = null,
        string? uplinkStatus = null,
        bool emagged = false,
        AntagonistKind? antagonist = null,
        IReadOnlyList<ChemicalEntry>? chemicals = null,
        bool dangerousChemicals = false)
    {
        var hasSyndicate = syndicate || syndicateGear || emagged ||
                           !string.IsNullOrEmpty(uplinkStatus) ||
                           implants is { Count: > 0 } ||
                           dangerousChemicals;

        return new PlayerSnapshot
        {
            Entity = Subject,
            Flags = Report(security, command, syndicate, syndicateGear,
                dangerousChemicals ? PlayerFlag.HasDangerousChemicals : extraFlag),
            Identity = new IdentityData(Subject, "Test Subject", string.Empty, "Passenger", null, null, true, 12.5f),
            Command = command
                ? new CommandData("HOP", "Head of Personnel", !noMindshield, commandItems ?? [])
                : null,
            Security = security ? new SecurityData(SecurityStatus.None, true) : null,
            Syndicate = hasSyndicate
                ? new SyndicateData(
                    uplinkStatus ?? (syndicate ? RobustaSyndicateDetectorSystem.OpenUplinkStatus : null),
                    syndicateGear,
                    emagged,
                    (implants ?? []).Select(name => new ImplantInfo(name, "Syndicate")).ToList(),
                    syndicateGear ? ["Emag"] : [],
                    syndicateGear,
                    syndicateGear || dangerousChemicals)
                : null,
            Antagonist = antagonist is { } kind ? new AntagonistData(kind, false, false, changeling) : null,
            Theft = theftItems is { Count: > 0 } ? new TheftData(theftItems) : null,
            Chemistry = chemicals is { Count: > 0 } || dangerousChemicals
                ? new ChemistryData(chemicals ?? [], [], dangerousChemicals)
                : null,
            Equipment = weapons is not null || nonLethals is not null || armor is not null
                ? new EquipmentData(weapons ?? [], nonLethals ?? [], armor ?? new Dictionary<string, float>())
                : null
        };
    }

    /// <summary>A report holding exactly the flags the test asked for, and nothing else.</summary>
    private static PlayerFlagReport Report(params PlayerFlag[] triggered)
    {
        var definitions = PlayerStateUtility.DefaultFlagDefinitions().ToDictionary(definition => definition.Flag);

        var results = PlayerFlag.Defaults
            .Select(flag =>
            {
                var definition = definitions[flag];
                return triggered.Contains(flag)
                    ? new PlayerFlagResult(flag, definition.Category, true,
                        [PlayerFlagTrigger.For(FlagTriggerSource.Component, "test")], definition.Display)
                    : PlayerFlagResult.Miss(flag, definition.Category);
            })
            .ToList();

        return new PlayerFlagReport(results);
    }

    private static PlayerFlagReport Report(
        bool security = false,
        bool command = false,
        bool syndicate = false,
        bool syndicateGear = false,
        PlayerFlag? extraFlag = null)
    {
        var flags = new List<PlayerFlag>();
        if (security)
            flags.Add(PlayerFlag.IsSecurity);
        if (command)
            flags.Add(PlayerFlag.IsCommand);
        if (syndicate)
            flags.Add(PlayerFlag.IsSyndicate);
        if (syndicateGear)
            flags.Add(PlayerFlag.HasSyndicateGear);
        if (extraFlag is { } extra)
            flags.Add(extra);

        return Report(flags.ToArray());
    }

    private static Dictionary<string, float> Armor(params (string Type, float Coefficient)[] entries) =>
        entries.ToDictionary(entry => entry.Type, entry => entry.Coefficient, StringComparer.OrdinalIgnoreCase);

    /// <summary>The dropdown that nests a detail block under the antag header.</summary>
    private static InfoRow Dropdown(PlayerInfoReport report, string label) =>
        report.Sections
            .Single(s => s.Kind == InfoSectionKind.Syndicate)
            .Rows.Single(r => r.Label == label);

    [Fact]
    public void GeneralAlwaysRenders_FlagsOnlyWhenOneFired()
    {
        // A plain passenger trips no flag worth listing, so GENERAL stands alone.
        var plain = RobustaPlayerInfoSystem.Compose(Snapshot());
        plain.Sections.Select(s => s.Kind).Should().ContainSingle().Which.Should().Be(InfoSectionKind.General);

        // NoSlips is a flag with no dedicated section, so it earns GENERAL FLAGS.
        var withFlag = RobustaPlayerInfoSystem.Compose(Snapshot(extraFlag: PlayerFlag.HasNoSlips));
        withFlag.Sections.Select(s => s.Kind).Should().Contain(InfoSectionKind.Flags);
    }

    [Fact]
    public void Compose_WithoutIdentity_SaysSoInsteadOfShowingUnknownRows()
    {
        // A snapshot with no blocks at all used to render a General section of "Unknown" rows, which
        // reads as a broken panel. It must say so explicitly instead.
        var report = RobustaPlayerInfoSystem.Compose(new PlayerSnapshot { Entity = Subject });

        report.Sections.Should().ContainSingle();
        report.Sections[0].Rows.Should().ContainSingle()
            .Which.Label.Should().Be(RobustaPlayerInfoSystem.NoStateText);
    }

    [Fact]
    public void Compose_WithIdentity_StillBuildsTheGeneralSection()
    {
        var report = RobustaPlayerInfoSystem.Compose(Snapshot());

        report.Sections.Should().NotBeEmpty();
        report.Sections[0].Rows.Should().Contain(row => row.Label == "Name");
        report.Sections.SelectMany(section => section.Rows)
            .Should().NotContain(row => row.Label == RobustaPlayerInfoSystem.NoStateText);
    }

    [Fact]
    public void HeldItemLookup_IsVersionTolerant()
    {
        // Upstream's GetHeldItem has an optional hideVirtualItems parameter that GoobStation lacks.
        // An optional parameter is baked into the call site, so a direct call emits a three-argument
        // reference and throws there. The lookup must go through reflection instead.
        typeof(ItemUtilitySystem)
            .GetMethod("GetHeldInHand", BindingFlags.Instance | BindingFlags.Public)
            .Should()
            .NotBeNull("the version-tolerant lookup must stay public for the viewer to use");
    }

    [Fact]
    public void SecurityAndCommand_AppearOnlyWhenTheyHaveSomethingToSay()
    {
        // The department alone is not enough: an officer carrying nothing disabling has no SECURITY
        // section to show, and a mindshielded head with no items has no COMMAND one.
        var bare = RobustaPlayerInfoSystem.Compose(Snapshot(security: true, command: true));
        bare.Sections.Select(s => s.Kind).Should().NotContain(InfoSectionKind.Security);
        bare.Sections.Select(s => s.Kind).Should().NotContain(InfoSectionKind.Command);

        var withContent = RobustaPlayerInfoSystem.Compose(Snapshot(
            security: true,
            command: true,
            nonLethals: [new WeaponEntry("Stun baton", 40f, new AmmunitionState(), false)],
            commandItems: [new CommandItemEntry("RD", "Research Director's ID Card")]));

        withContent.Sections.Select(s => s.Kind).Should().Contain(InfoSectionKind.Security);
        withContent.Sections.Select(s => s.Kind).Should().Contain(InfoSectionKind.Command);
    }

    [Fact]
    public void Command_FlagsACommandTargetWithNoMindshield()
    {
        var unshielded = RobustaPlayerInfoSystem.Compose(Snapshot(
            command: true,
            noMindshield: true,
            commandItems: [new CommandItemEntry("HOP", "Head of Personnel's ID Card")]));

        // The alert is the first line of the dropdown, above the item list.
        var command = unshielded.Sections.Single(s => s.Kind == InfoSectionKind.Command);
        command.Rows[0].Should().Be(new InfoRow("NO MINDSHIELD", string.Empty));
        command.Rows.Should().Contain(r => r.Label == "Command items");

        // A mindshielded head is ordinary command and gets no alert row.
        var shielded = RobustaPlayerInfoSystem.Compose(Snapshot(
            command: true,
            commandItems: [new CommandItemEntry("HOP", "Head of Personnel's ID Card")]));

        shielded.Sections.Single(s => s.Kind == InfoSectionKind.Command)
            .Rows.Should().NotContain(r => r.Label == "NO MINDSHIELD");
    }

    [Fact]
    public void ArmorAndWeapons_LiveInGeneralOnce()
    {
        var report = RobustaPlayerInfoSystem.Compose(Snapshot(
            security: true,
            command: true,
            armor: Armor(("Blunt", 0.7f)),
            weapons: [new WeaponEntry("Laser rifle", 20f, new AmmunitionState(12, 12), true)]));

        var general = report.Sections.Single(s => s.Kind == InfoSectionKind.General);
        general.Rows.Select(r => r.Label).Should().Contain("Armor").And.Contain("Weapons");

        // Other sections must not repeat them.
        foreach (var section in report.Sections.Where(s => s.Kind != InfoSectionKind.General))
        {
            section.Rows.Select(r => r.Label).Should().NotContain("Armor");
            section.Rows.Select(r => r.Label).Should().NotContain("Weapons");
        }
    }

    [Fact]
    public void PlainRows_AreOrderedAboveDropdowns()
    {
        // A strip the reader can take in at a glance must not sit under a control they have to click.
        var report = RobustaPlayerInfoSystem.Compose(Snapshot(
            armor: Armor(("Blunt", 0.7f)),
            weapons: [new WeaponEntry("Laser rifle", 20f, new AmmunitionState(12, 12), true)]));

        var general = report.Sections.Single(s => s.Kind == InfoSectionKind.General);
        var weaponIndex = general.Rows.ToList().FindIndex(r => r.Label == "Weapons");
        var armorIndex = general.Rows.ToList().FindIndex(r => r.Label == "Armor");

        weaponIndex.Should().BeGreaterThan(-1);
        armorIndex.Should().BeLessThan(weaponIndex);
        general.Rows[^1].Children.Should().NotBeNull("the dropdowns all follow the plain strips");
    }

    [Fact]
    public void SyndicateSection_AppearsForGearEvenWithoutTheRoleFlag()
    {
        var report = RobustaPlayerInfoSystem.Compose(Snapshot(syndicateGear: true));

        report.Sections.Select(s => s.Kind).Should().Contain(InfoSectionKind.Syndicate);
    }

    [Fact]
    public void SyndicateSection_IsOmittedWhenNothingPointsThatWay()
    {
        var report = RobustaPlayerInfoSystem.Compose(Snapshot());

        report.Sections.Select(s => s.Kind).Should().NotContain(InfoSectionKind.Syndicate);
    }

    [Fact]
    public void SyndicateSection_OmitsTheStatusRowWhenNothingFired()
    {
        // Implants alone earn the section, with no uplink and no contraband there is no status to
        // report, and "None" under a SYNDICATE header contradicts the header.
        var report = RobustaPlayerInfoSystem.Compose(Snapshot(implants: ["MicroBomb"]));
        var section = report.Sections.Single(s => s.Kind == InfoSectionKind.Syndicate);

        section.Rows.Select(r => r.Label).Should().NotContain("Status");
        section.Rows.Should().Contain(r => r.Label == "Implants");
    }

    [Fact]
    public void SyndicateSection_ReportsAnOpenUplinkAndAnEmag()
    {
        var report = RobustaPlayerInfoSystem.Compose(Snapshot(syndicate: true, emagged: true));
        var section = report.Sections.Single(s => s.Kind == InfoSectionKind.Syndicate);

        section.Rows.Single(r => r.Label == "Status").Value
            .Should().Be($"{RobustaSyndicateDetectorSystem.OpenUplinkStatus}, EMAGGED");
    }

    [Fact]
    public void SyndicateSection_KeepsAClosedUplinkOnScreen()
    {
        var report = RobustaPlayerInfoSystem.Compose(
            Snapshot(uplinkStatus: RobustaSyndicateDetectorSystem.ClosedUplinkStatus));
        var section = report.Sections.Single(s => s.Kind == InfoSectionKind.Syndicate);

        section.Rows.Single(r => r.Label == "Status").Value.Should().Be("CLOSED UPLINK");
    }

    [Fact]
    public void Theft_LivesInItsOwnDropdownUnderTheAntagHeader()
    {
        // The dropdown is the evidence behind the radar's [THEFT?] tag, so it has to name the items
        // and say which tier flagged each one.
        var report = RobustaPlayerInfoSystem.Compose(Snapshot(theftItems:
        [
            new TheftEntry("Hypospray", "GrandTheft"),
            new TheftEntry("Handheld Crew Monitor", "Restricted")
        ]));

        var theft = Dropdown(report, "Theft");

        theft.Value.Should().Be("2");
        theft.Children!.Select(r => r.Label).Should().BeEquivalentTo(["Grand Theft", "Restricted"]);
        theft.Children!.Should().OnlyContain(r => r.Children != null && r.Children.Count > 0);
    }

    [Fact]
    public void Theft_IsOmittedWithoutItems()
    {
        var report = RobustaPlayerInfoSystem.Compose(Snapshot());

        report.Sections
            .SelectMany(s => s.Rows)
            .Should().NotContain(r => r.Label == "Theft");
    }

    [Fact]
    public void Theft_AloneEarnsTheAntagHeader()
    {
        // Stolen gear nests under the antag header, so it has to raise that header by itself.
        var report = RobustaPlayerInfoSystem.Compose(
            Snapshot(theftItems: [new TheftEntry("Hypospray", "GrandTheft")]));

        Dropdown(report, "Theft").Children.Should().ContainSingle();
    }

    [Fact]
    public void ChemicalSection_IsOmittedWithoutContents()
    {
        var report = RobustaPlayerInfoSystem.Compose(Snapshot());

        report.Sections.Select(s => s.Kind).Should().NotContain(InfoSectionKind.Chemical);
    }

    [Fact]
    public void ChemicalSection_PrefixesTheHoldersWorstSeverity()
    {
        var report = RobustaPlayerInfoSystem.Compose(Snapshot(chemicals:
        [
            new ChemicalEntry("Beaker", "Toxin", 5f, "Major"),
            new ChemicalEntry("Beaker", "Water", 5f, "Minor")
        ]));

        var section = report.Sections.Single(s => s.Kind == InfoSectionKind.Chemical);
        var contents = section.Rows.Single(r => r.Label == "Contents");

        contents.Children!.Single().Label.Should().Be("[MAJOR CONTRA] Beaker");
        contents.Children!.Single().Children!.Select(r => r.Label)
            .Should().BeEquivalentTo(["Toxin", "Water"]);
    }

    [Fact]
    public void Changeling_IsOmittedWithoutEvidence()
    {
        var report = RobustaPlayerInfoSystem.Compose(Snapshot());

        report.Sections
            .SelectMany(s => s.Rows)
            .Should().NotContain(r => r.Label == "Changeling");
    }

    [Fact]
    public void Changeling_ShowsTriggersPurchasesAndDna()
    {
        var report = RobustaPlayerInfoSystem.Compose(Snapshot(
            antagonist: AntagonistKind.Changeling,
            changeling: new ChangelingData(
                ["Granted actions", "DNA store"],
                ["ActionRetractableItemArmblade", "ActionChangelingStingLead"],
                false,
                35,
                null)));

        var rows = Dropdown(report, "Changeling").Children!;

        rows.Single(r => r.Label == "Status").Value.Should().Be("Granted actions, DNA store");
        rows.Single(r => r.Label == "DNA").Value.Should().Be("35");

        var purchased = rows.Single(r => r.Label == "Purchased");
        purchased.Value.Should().Be("2");
        purchased.Children!.Select(r => r.Label).Should().Equal("Armblade", "Lethal Sting");
    }

    [Fact]
    public void Changeling_ShowsTheHorrorCountdown()
    {
        var report = RobustaPlayerInfoSystem.Compose(Snapshot(
            antagonist: AntagonistKind.Changeling,
            changeling: new ChangelingData(["Horror form"], [], false, null, TimeSpan.FromSeconds(134))));

        Dropdown(report, "Changeling").Children!
            .Single(r => r.Label == "Horror form").Value.Should().Be("2m 14s left");
    }

    [Fact]
    public void ChangelingPurchases_NameOnlyWhatTheStoreSells()
    {
        // The base kit is granted on assignment, never bought, so it must not read as a purchase.
        var names = RobustaPlayerInfoSystem.NameChangelingPurchases(
        [
            "ActionChangelingStore",
            "ActionChangelingDevour",
            "ActionChangelingTransform",
            "ActionChangelingStasis",
            "ActionChangelingScreech",
            "ActionChangelingTakeOverCorpse",
            "ActionChangelingStingLead",
            "ActionRetractableItemArmblade"
        ]);

        names.Should().Equal("Armblade", "Lethal Sting");
    }

    [Fact]
    public void ChangelingPurchases_IncludeFleshClothing()
    {
        // Flesh clothing grants a component rather than an action, so it arrives as a flag.
        RobustaPlayerInfoSystem.NameChangelingPurchases([], fleshClothing: true)
            .Should().Equal("Flesh Clothing");
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(-5, "0s")]
    [InlineData(45, "45s")]
    [InlineData(134, "2m 14s")]
    public void FormatDuration_ReadsAsACountdown(int seconds, string expected)
    {
        RobustaPlayerInfoSystem.FormatDuration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Theory]
    [InlineData(12.5f, "12.5m")]
    [InlineData(0f, "0.0m")]
    public void Range_IsFormatted(float meters, string expected)
    {
        RobustaPlayerInfoSystem.FormatRange(meters).Should().Be(expected);
    }

    [Fact]
    public void Range_FallsBackToNaWithoutAnAttachedPlayer()
    {
        RobustaPlayerInfoSystem.FormatRange(null).Should().Be(RobustaPlayerInfoSystem.NoRangeText);
    }

    [Fact]
    public void ArmorGroups_CombineTypesUnderTheirGroup()
    {
        var rows = RobustaPlayerInfoSystem.CombineArmorGroups(
            Armor(("Blunt", 0.7f), ("Slash", 0.7f), ("Piercing", 0.7f)));

        rows.Should().Equal(new InfoRow("BRUTE", "Blunt 30%, Slash 30%, Piercing 30%"));
    }

    [Fact]
    public void ArmorGroups_SkipTypesWithNoReduction()
    {
        var rows = RobustaPlayerInfoSystem.CombineArmorGroups(
            Armor(("Blunt", 1f), ("Heat", 0.5f)));

        // BRUTE has nothing worth a row, so it does not appear as an empty heading.
        rows.Should().Equal(new InfoRow("BURN", "Heat 50%"));
    }

    [Fact]
    public void ArmorGroups_IncludeTypesNoGroupNames()
    {
        // A damage type upstream adds later still shows rather than being silently dropped.
        var rows = RobustaPlayerInfoSystem.CombineArmorGroups(Armor(("SomeFutureType", 0.5f)));

        rows.Should().Equal(new InfoRow("OTHER", "SomeFutureType 50%"));
    }

    [Fact]
    public void ArmorGroups_WithNothingWornAreEmpty()
    {
        RobustaPlayerInfoSystem.CombineArmorGroups(new Dictionary<string, float>()).Should().BeEmpty();
    }

    [Fact]
    public void FlagLabel_ReadsAsPlainWords()
    {
        RobustaPlayerInfoSystem.FlagLabel("HasNoSlips").Should().Be("No Slips");
        RobustaPlayerInfoSystem.FlagLabel("HasWeapon").Should().Be("Weapon");
        RobustaPlayerInfoSystem.FlagLabel("IsSecurity").Should().Be("Security");
        RobustaPlayerInfoSystem.FlagLabel("HasMind").Should().Be("Mind");
    }

    [Theory]
    [InlineData("ClothingNeckCloakRd", "RD")]
    [InlineData("ClothingOuterHardsuitRd", "RD")]
    [InlineData("BoxFolderQmClipboard", "QM")]
    [InlineData("ClothingHandsKnuckleDustersQM", "QM")]
    [InlineData("RubberStampQm", "QM")]
    [InlineData("RubberStampRd", "RD")]
    [InlineData("CaptainIDCard", "CAPTAIN")]
    [InlineData("NukeDisk", "CAPTAIN")]
    [InlineData("ClothingNeckCloakCap", "CAPTAIN")]
    [InlineData("HoPIDCard", "HOP")]
    [InlineData("ClothingNeckCloakHop", "HOP")]
    [InlineData("RubberStampHop", "HOP")]
    [InlineData("DoorRemoteCommand", "COMMAND")]
    [InlineData("EncryptionKeyCommand", "COMMAND")]
    [InlineData("EncryptionKeyStationMaster", "COMMAND")]
    public void CommandRole_MapsPrototypeToItsRole(string prototypeId, string expected)
    {
        PlayerStateUtility.ResolveCommandRole(prototypeId).Should().Be(expected);
    }

    [Theory]
    [InlineData("Toolbox")]
    [InlineData("ClothingUniformJumpsuitPassenger")]
    [InlineData("Crowbar")]
    // A department key belongs to its department, not to command.
    [InlineData("EncryptionKeyMedical")]
    [InlineData("EncryptionKeySecurity")]
    [InlineData("EncryptionKeyCargo")]
    public void CommandRole_ReturnsNullForOrdinaryGear(string prototypeId)
    {
        PlayerStateUtility.ResolveCommandRole(prototypeId).Should().BeNull();
    }

    /// <summary>
    /// The table is matched against a lowercased prototype id, so an entry with a capital would
    /// silently never match.
    /// </summary>
    [Fact]
    public void CommandRole_MatchesCaseInsensitively()
    {
        PlayerStateUtility.ResolveCommandRole("CLOTHINGNECKCLOAKRD").Should().Be("RD");
        PlayerStateUtility.ResolveCommandRole("clothingneckcloakrd").Should().Be("RD");
    }

    /// <summary>
    /// The radar raises its hedged tag from a dangerous reagent alone, so the panel must not drop the
    /// section. This was the [SYNDICATE?] tag with nothing behind it.
    /// </summary>
    [Fact]
    public void DangerousReagentAlone_RendersBothSections()
    {
        var report = RobustaPlayerInfoSystem.Compose(Snapshot(dangerousChemicals: true));

        report.Sections.Select(section => section.Kind).Should()
            .Contain(InfoSectionKind.Syndicate)
            .And.Contain(InfoSectionKind.Chemical);

        report.Sections.Single(section => section.Kind == InfoSectionKind.Chemical)
            .Rows.Should().NotBeEmpty();
    }

    /// <summary>
    /// Every signal that raises the radar's hedged tag must also open the syndicate section, or the
    /// tag has nothing explaining it.
    /// </summary>
    [Theory]
    [InlineData(true, false, 0, false, false, true)]
    [InlineData(false, true, 0, false, false, true)]
    [InlineData(false, false, 1, false, false, true)]
    [InlineData(false, false, 0, true, false, true)]
    [InlineData(false, false, 0, false, true, true)]
    // Nothing to report: the section stays away.
    [InlineData(false, false, 0, false, false, false)]
    public void ShouldBuildSyndicate_MatchesEveryRaisingSignal(
        bool dangerousChemicals,
        bool contraband,
        int implants,
        bool gear,
        bool emagged,
        bool expected)
    {
        PlayerStateUtility.ShouldBuildSyndicate(
                uplinkStatus: null,
                hasContraband: contraband,
                implantCount: implants,
                hasSyndicateGear: gear,
                emagged: emagged,
                hasDangerousChemicals: dangerousChemicals)
            .Should()
            .Be(expected);
    }

    /// <summary>An open uplink alone is enough, which is the original latched case.</summary>
    [Fact]
    public void ShouldBuildSyndicate_AcceptsAnUplinkAlone()
    {
        PlayerStateUtility.ShouldBuildSyndicate(
                RobustaSyndicateDetectorSystem.OpenUplinkStatus, false, 0, false, false, false)
            .Should()
            .BeTrue();
    }
}
