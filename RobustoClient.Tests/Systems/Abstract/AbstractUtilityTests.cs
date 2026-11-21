using System;
using Content.Shared.Mobs.Components;
using FluentAssertions;
using Robust.Shared.GameObjects;
using RobustoClient.Systems.Abstract;
using Xunit;

namespace RobustoClient.Tests.Systems.Abstract;

/// <summary>
/// The cache sweep and the cursor filter are the two pieces of the abstract utilities that need no
/// client, so they carry the coverage.
/// </summary>
public sealed class CacheUtilityTests
{
    private static TimeSpan Stamp((int Id, TimeSpan Stamp) entry) => entry.Stamp;

    [Fact]
    public void RemoveOlderThan_DropsOnlyExpiredEntries()
    {
        var cache = new Dictionary<int, (int Id, TimeSpan Stamp)>
        {
            [1] = (1, TimeSpan.FromSeconds(1)),
            [2] = (2, TimeSpan.FromSeconds(5)),
            [3] = (3, TimeSpan.FromSeconds(9)),
        };

        var removed = CacheUtility.RemoveOlderThan(cache, Stamp, TimeSpan.FromSeconds(4));

        removed.Should().Be(1);
        cache.Keys.Should().BeEquivalentTo([2, 3]);
    }

    [Fact]
    public void RemoveOlderThan_KeepsEverythingWhenNothingExpired()
    {
        // The crowded-room case: every entry is fresh, so a sweep must not evict the mobs that the
        // next frame is about to ask for again.
        var cache = new Dictionary<int, (int Id, TimeSpan Stamp)>
        {
            [1] = (1, TimeSpan.FromSeconds(9)),
            [2] = (2, TimeSpan.FromSeconds(9)),
        };

        CacheUtility.RemoveOlderThan(cache, Stamp, TimeSpan.FromSeconds(4)).Should().Be(0);
        cache.Should().HaveCount(2);
    }

    [Fact]
    public void RemoveOldest_RemovesTheLowestStampOnly()
    {
        var cache = new Dictionary<int, (int Id, TimeSpan Stamp)>
        {
            [1] = (1, TimeSpan.FromSeconds(7)),
            [2] = (2, TimeSpan.FromSeconds(2)),
            [3] = (3, TimeSpan.FromSeconds(5)),
        };

        CacheUtility.RemoveOldest(cache, Stamp).Should().BeTrue();

        cache.Keys.Should().BeEquivalentTo([1, 3]);
    }

    [Fact]
    public void RemoveOldest_OnEmptyCache_ReportsNothingRemoved()
    {
        var cache = new Dictionary<int, (int Id, TimeSpan Stamp)>();

        CacheUtility.RemoveOldest(cache, Stamp).Should().BeFalse();
    }
}

public class MouseUtilityTests
{
    private static readonly EntityUid Entity = new(1);

    [Fact]
    public void DefaultRange_IsOneMetre()
    {
        MouseUtilitySystem.DefaultRange.Should().Be(1f);
    }

    [Fact]
    public void PassesFilter_NoLists_AcceptsEverything()
    {
        MouseUtilitySystem.PassesFilter(Entity, null, null, (_, _) => false).Should().BeTrue();
    }

    [Fact]
    public void PassesFilter_Whitelist_RequiresAnyListedComponent()
    {
        var has = (EntityUid _, Type type) => type == typeof(MobStateComponent);

        MouseUtilitySystem.PassesFilter(Entity, [typeof(MobStateComponent)], null, has).Should().BeTrue();
        MouseUtilitySystem.PassesFilter(Entity, [typeof(TransformComponent)], null, has).Should().BeFalse();
    }

    [Fact]
    public void PassesFilter_Exclude_RejectsListedComponent()
    {
        var has = (EntityUid _, Type type) => type == typeof(MobStateComponent);

        MouseUtilitySystem.PassesFilter(Entity, null, [typeof(MobStateComponent)], has).Should().BeFalse();
    }

    [Fact]
    public void PassesFilter_Exclude_WinsOverWhitelist()
    {
        var has = (EntityUid _, Type type) => type == typeof(MobStateComponent);

        MouseUtilitySystem
            .PassesFilter(Entity, [typeof(MobStateComponent)], [typeof(MobStateComponent)], has)
            .Should().BeFalse();
    }

    [Fact]
    public void TryAcceptCapturedEntity_AppliesWhitelistAndExcludeFilters()
    {
        var has = (EntityUid _, Type type) => type == typeof(TransformComponent);

        MouseUtilitySystem.TryAcceptCapturedEntity(
            Entity,
            _ => true,
            has,
            [typeof(MobStateComponent)],
            null,
            out _).Should().BeFalse();

        MouseUtilitySystem.TryAcceptCapturedEntity(
            Entity,
            _ => true,
            has,
            null,
            [typeof(TransformComponent)],
            out _).Should().BeFalse();

        MouseUtilitySystem.TryAcceptCapturedEntity(
            Entity,
            _ => true,
            has,
            [typeof(TransformComponent)],
            null,
            out var accepted).Should().BeTrue();
        accepted.Should().Be(Entity);
    }
}