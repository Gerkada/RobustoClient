using System;
using Content.Shared.Contraband;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using FluentAssertions;
using Robust.Shared.Localization;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using RobustoClient.Reflection;
using Xunit;

namespace RobustoClient.Tests.Reflection;

/// <summary>
/// <see cref="Reflect"/> is pure reflection over local types, so the whole lookup matrix is reachable
/// without a client. That is the point of keeping it free of ECS and game knowledge.
/// </summary>
public class ReflectTests
{
    private sealed class Sample
    {
        public int PublicField = 5;
        public string PublicProperty { get; set; } = "hello";
        public string Renamed = "second";
        public string ReadOnly { get; } = "fixed";
        public Nested Nested { get; } = new();

        private int _privateField = 9;
        private string _nodes = "underscore";

        public int Add(int a, int b) => a + b;
        public int Overloaded(int a) => a;
        public int Overloaded(int a, int b) => a + b;
        public string Injected(string name, int count) => $"{name}:{count}";
        public string InjectedNullable(int? value) => value?.ToString() ?? "null";
        public string InjectedInterface(IComparable value) => value.ToString()!;
    }

    private sealed class Nested
    {
        public int Value { get; } = 7;
    }

    [Fact]
    public void PublicField_IsRead() => Reflect.Get(new Sample(), "PublicField").Should().Be(5);

    [Fact]
    public void PublicProperty_IsRead() => Reflect.Get(new Sample(), "PublicProperty").Should().Be("hello");

    [Fact]
    public void PrivateField_NeedsNonPublic()
    {
        Reflect.Get(new Sample(), "_privateField").Should().BeNull();
        Reflect.Get(new Sample(), "_privateField", ReflectOptions.Any).Should().Be(9);
    }

    [Fact]
    public void IgnoreCase_FindsTheMember() =>
        Reflect.Get(new Sample(), "publicfield", ReflectOptions.Any).Should().Be(5);

    [Fact]
    public void UnderscoreFallback_FindsThePrivateField() =>
        // The convention for a private backing field behind a public property.
        Reflect.Get(new Sample(), "nodes", ReflectOptions.Any).Should().Be("underscore");

    [Fact]
    public void DottedPath_TraversesNestedMembers() =>
        Reflect.Get(new Sample(), "Nested.Value").Should().Be(7);

    [Fact]
    public void MissingMember_ReturnsFalseWithoutThrowing()
    {
        Reflect.TryGet(new Sample(), "NoSuchMember", out var value).Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void NullTarget_ReturnsFalse() => Reflect.TryGet(null, "Anything", out _).Should().BeFalse();

    [Fact]
    public void GetFirst_FallsThroughToTheSecondName() =>
        // The renamed-upstream-field case: the old name is gone, the new one answers.
        Reflect.GetFirst(new Sample(), "Alpha", "Renamed").Should().Be("second");

    [Fact]
    public void GetFirst_ReturnsNullWhenNoneMatch() =>
        Reflect.GetFirst(new Sample(), "Alpha", "Beta").Should().BeNull();

    [Fact]
    public void TrySet_WritesASettableProperty()
    {
        var sample = new Sample();

        Reflect.TrySet(sample, "PublicProperty", "changed").Should().BeTrue();
        sample.PublicProperty.Should().Be("changed");
    }

    [Fact]
    public void TrySet_WritesAField()
    {
        var sample = new Sample();

        Reflect.TrySet(sample, "PublicField", 11).Should().BeTrue();
        sample.PublicField.Should().Be(11);
    }

    [Fact]
    public void TrySet_WritesAGetOnlyPropertyThroughItsBackingField()
    {
        var sample = new Sample();

        Reflect.TrySet(sample, "ReadOnly", "forced").Should().BeTrue();
        sample.ReadOnly.Should().Be("forced");
    }

    [Fact]
    public void TrySet_MissingMember_ReturnsFalse() =>
        Reflect.TrySet(new Sample(), "NoSuchMember", 1).Should().BeFalse();

    [Fact]
    public void TryInvoke_PassesArguments()
    {
        Reflect.TryInvoke(new Sample(), "Add", out var result, 2, 3).Should().BeTrue();
        result.Should().Be(5);
    }

    [Fact]
    public void TryInvoke_SelectsTheOverloadByParameterTypes()
    {
        var sample = new Sample();

        Reflect.TryInvoke(sample, "Overloaded", [typeof(int)], out var one, 7).Should().BeTrue();
        one.Should().Be(7);

        Reflect.TryInvoke(sample, "Overloaded", [typeof(int), typeof(int)], out var two, 7, 8).Should().BeTrue();
        two.Should().Be(15);
    }

    [Fact]
    public void TryInvokeInjected_ResolvesByExactType()
    {
        var context = new ReflectContext().Add("name").Add(3);

        Reflect.TryInvokeInjected(new Sample(), "Injected", context, out var result).Should().BeTrue();
        result.Should().Be("name:3");
    }

    [Fact]
    public void TryInvokeInjected_ResolvesANullableFromItsUnderlyingType()
    {
        var context = new ReflectContext().Add(4);

        Reflect.TryInvokeInjected(new Sample(), "InjectedNullable", context, out var result).Should().BeTrue();
        result.Should().Be("4");
    }

    [Fact]
    public void TryInvokeInjected_ResolvesByAssignability()
    {
        var context = new ReflectContext().Add("text");

        Reflect.TryInvokeInjected(new Sample(), "InjectedInterface", context, out var result).Should().BeTrue();
        result.Should().Be("text");
    }

    [Fact]
    public void TryInvokeInjected_FailsWhenAParameterCannotBeResolved() =>
        Reflect.TryInvokeInjected(new Sample(), "Injected", new ReflectContext(), out _).Should().BeFalse();

    [Fact]
    public void GetMethod_WithParameterTypes_FindsTheOverload()
    {
        var method = Reflect.GetMethod(typeof(Sample), "Overloaded", [typeof(int), typeof(int)]);

        method.Should().NotBeNull();
        method!.GetParameters().Should().HaveCount(2);
    }

    [Fact]
    public void HasAnyMember_ReportsPresence()
    {
        Reflect.HasAnyMember(typeof(Sample), ["Alpha", "PublicField"]).Should().BeTrue();
        Reflect.HasAnyMember(typeof(Sample), ["Alpha", "Beta"]).Should().BeFalse();
    }

    [Fact]
    public void EnumerateMembers_IncludesFieldsAndProperties()
    {
        var names = Reflect.EnumerateMembers(typeof(Sample)).Select(m => m.Name).ToList();

        names.Should().Contain("PublicField");
        names.Should().Contain("PublicProperty");
        names.Should().NotContain("_privateField");
    }

    [Fact]
    public void FindType_ResolvesAKnownType() =>
        Reflect.FindType("RobustoClient.Reflection.Reflect").Should().Be(typeof(Reflect));

    [Fact]
    public void FindType_ReturnsNullForABogusName() =>
        Reflect.FindType("No.Such.Namespace.NoSuchType").Should().BeNull();
}

/// <summary>
/// The coercion table is what lets a declarative value reach a member of a different type, so each
/// row is pinned here rather than discovered in game.
/// </summary>
public class ReflectConvertTests
{
    private enum Sample
    {
        Alpha,
        Beta
    }

    [Fact]
    public void Bool_FromBool()
    {
        ReflectConvert.TryCoerce(true, typeof(bool), out var coerced).Should().BeTrue();
        coerced.Should().Be(true);
    }

    [Fact]
    public void Bool_FromString()
    {
        ReflectConvert.TryCoerce("true", typeof(bool), out var coerced).Should().BeTrue();
        coerced.Should().Be(true);
    }

    [Fact]
    public void Enum_FromNameIgnoringCase()
    {
        ReflectConvert.TryCoerce("beta", typeof(Sample), out var coerced).Should().BeTrue();
        coerced.Should().Be(Sample.Beta);
    }

    [Fact]
    public void ProtoId_FromString()
    {
        ReflectConvert.TryCoerce("GrandTheft", typeof(ProtoId<ContrabandSeverityPrototype>), out var coerced)
            .Should().BeTrue();

        coerced.Should().Be(new ProtoId<ContrabandSeverityPrototype>("GrandTheft"));
    }

    [Fact]
    public void LocId_FromString()
    {
        ReflectConvert.TryCoerce("foo", typeof(LocId), out var coerced).Should().BeTrue();
        coerced.Should().Be(new LocId("foo"));
    }

    [Fact]
    public void Numeric_CrossType()
    {
        ReflectConvert.TryCoerce(5, typeof(float), out var coerced).Should().BeTrue();
        coerced.Should().Be(5f);
    }

    [Fact]
    public void FixedPoint_FromFloat()
    {
        ReflectConvert.TryCoerce(14.5f, typeof(FixedPoint2), out var coerced).Should().BeTrue();
        coerced.Should().Be(FixedPoint2.New(14.5f));
    }

    [Fact]
    public void FixedPoint2_IsResolvedByNameNotByAssembly()
    {
        // FixedPoint2 lives in Content.Shared on Wizden and Content.Common on GoobStation. A static
        // reference to it bakes the assembly into every signature that mentions it, so the type is
        // matched by name and constructed reflectively.
        var type = typeof(FixedPoint2);
        type.FullName.Should().Be("Content.Shared.FixedPoint.FixedPoint2");

        // The conversion path must work without naming the type.
        ReflectConvert.TryCoerce(14.5f, type, out var coerced).Should().BeTrue();
        coerced.Should().Be(FixedPoint2.New(14.5f));

        // Damage totals go through the same reflective route.
        ReflectConvert.DamageTotal(new DamageSpecifier { DamageDict = { ["Blunt"] = 12 } })
            .Should().Be(12f);
    }

    [Fact]
    public void TimeSpan_FromSeconds()
    {
        ReflectConvert.TryCoerce(2.5f, typeof(TimeSpan), out var coerced).Should().BeTrue();
        coerced.Should().Be(TimeSpan.FromSeconds(2.5));
    }

    [Fact]
    public void Color_FromHex()
    {
        ReflectConvert.TryCoerce("#FF0000", typeof(Color), out var coerced).Should().BeTrue();
        coerced.Should().BeOfType<Color>();
    }

    [Fact]
    public void Nullable_UnwrapsToItsUnderlyingType()
    {
        ReflectConvert.TryCoerce(3, typeof(int?), out var coerced).Should().BeTrue();
        coerced.Should().Be(3);
    }

    [Fact]
    public void Null_ToReferenceType_Succeeds()
    {
        ReflectConvert.TryCoerce(null, typeof(string), out var coerced).Should().BeTrue();
        coerced.Should().BeNull();
    }

    [Fact]
    public void Null_ToValueType_Fails()
    {
        ReflectConvert.TryCoerce(null, typeof(int), out _).Should().BeFalse();
    }

    [Fact]
    public void TryToDouble_ReadsFixedPointThroughItsFloatMethod()
    {
        ReflectConvert.TryToDouble(FixedPoint2.New(14.5f), out var result).Should().BeTrue();
        result.Should().BeApproximately(14.5, 1e-5);
    }

    [Theory]
    [InlineData(true)]
    [InlineData("14")]
    public void TryToDouble_RejectsNonNumbers(object value)
    {
        ReflectConvert.TryToDouble(value, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGetId_UnwrapsProtoId()
    {
        ReflectConvert.TryGetId(new ProtoId<ContrabandSeverityPrototype>("GrandTheft")).Should().Be("GrandTheft");
    }

    [Fact]
    public void TryGetId_PassesStringsThrough()
    {
        ReflectConvert.TryGetId("plain").Should().Be("plain");
    }
}