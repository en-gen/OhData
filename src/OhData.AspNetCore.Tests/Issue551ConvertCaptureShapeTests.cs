using System;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class C551Model { public int Id { get; set; } public string Size { get; set; } = ""; }
public sealed class C551Entity { public int Id { get; set; } public int Size { get; set; } }

// One shape per profile: the refusal fires at construction, so each has to be constructed alone.
public sealed class C551InstanceMethodGroup : DeltaProfile
{
    private int ParseSize(string s) => int.Parse(s);
    public C551InstanceMethodGroup() =>
        For<C551Model, C551Entity>().Convert(m => m.Size, e => e.Size, ParseSize);
}

public sealed class C551CapturedLocal : DeltaProfile
{
    public C551CapturedLocal()
    {
        int radix = 10;
        For<C551Model, C551Entity>().Convert(m => m.Size, e => e.Size, s => Convert.ToInt32(s, radix));
    }
}

public sealed class C551NonCapturingLambda : DeltaProfile
{
    public C551NonCapturingLambda() =>
        For<C551Model, C551Entity>().Convert(m => m.Size, e => e.Size, s => int.Parse(s));
}

public sealed class C551StaticLambda : DeltaProfile
{
    public C551StaticLambda() =>
        For<C551Model, C551Entity>().Convert(m => m.Size, e => e.Size, static s => int.Parse(s));
}

public sealed class C551StaticMethodGroup : DeltaProfile
{
    private static int ParseSize(string s) => int.Parse(s);
    public C551StaticMethodGroup() =>
        For<C551Model, C551Entity>().Convert(m => m.Size, e => e.Size, ParseSize);
}

/// <summary>
/// #551 — the five converter shapes, pinned in both directions.
/// <para>
/// #488/#535 refuse a capturing <c>Convert()</c> converter, deliberately more broadly than
/// "captures a dependency": a delegate is opaque, so "captures nothing" is the only property
/// checkable from outside it. Two of the refused shapes surprise people — a private INSTANCE
/// helper on the profile and a captured local — and neither touches a dependency.
/// </para>
/// <para>
/// The ACCEPTANCE half is the half that had no coverage, and it is what stops a later "hardening"
/// of the check from refusing a plain non-capturing lambda. <c>docs/delta-mapping.md</c> and the
/// 1.7.0 CHANGELOG entry both publish this table, so it is pinned rather than asserted in prose.
/// </para>
/// </summary>
public sealed class Issue551ConvertCaptureShapeTests
{
    [Fact]
    public void InstanceMethodGroupOnTheProfile_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new C551InstanceMethodGroup());
        Assert.Contains("captures state from its enclosing scope", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Size'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CapturedLocal_IsRefused()
    {
        // Captures an immutable int and no dependency at all -- refused because C# compiles it into
        // a display class exactly as it would a captured DbContext.
        var ex = Assert.Throws<InvalidOperationException>(() => new C551CapturedLocal());
        Assert.Contains("captures state from its enclosing scope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonCapturingLambda_IsAccepted()
    {
        // The exception text recommends `static`, but Roslyn compiles a non-capturing lambda to a
        // cached fieldless singleton, so it is accepted. If this ever starts throwing, the docs and
        // the 1.7.0 entry are wrong too.
        new C551NonCapturingLambda();
    }

    [Fact]
    public void StaticLambda_IsAccepted() => new C551StaticLambda();

    [Fact]
    public void StaticMethodGroup_IsAccepted() => new C551StaticMethodGroup();
}
