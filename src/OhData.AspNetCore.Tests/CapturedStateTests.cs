using System;
using System.Linq.Expressions;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Direct coverage for <see cref="CapturedState.IsCapturedByExpression"/>'s constant judgment.
/// </summary>
/// <remarks>
/// <para>
/// #483 and #488 both turn on one line — <c>value is not null &amp;&amp; !value.GetType().IsValueType
/// &amp;&amp; value is not string</c> — and it was reachable only through the profile/cache seam,
/// which observes the CONSEQUENCE (is the compiled delegate shared) rather than the judgment. A
/// mutation run showed both <c>&amp;&amp;</c>s could become <c>||</c> with nothing objecting.
/// </para>
/// <para>
/// The judgment is not free in either direction. A false positive costs an
/// <c>Expression.Compile()</c> on <b>every request</b> that reaches the route — #483 measured
/// 2.5–3.5×, about +0.4 ms — while a false negative is the disposed/stale scoped dependency the
/// whole mechanism exists to prevent. So both directions are asserted here.
/// </para>
/// </remarks>
public class CapturedStateTests
{
    private sealed class Row
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public DayOfWeek Day { get; set; }
        public Row? Parent { get; set; }
    }

    private sealed class Dep
    {
        public string Stamp(string s) => s;
    }

    // Not a literal and not const, so the compiler cannot fold it into the lambda.
    private static string Outside() => DateTime.UtcNow.Ticks > 0 ? "outside" : "";

    /// <summary>
    /// A lambda that reads only its own parameter holds nothing, whatever constants it mentions.
    /// </summary>
    /// <remarks>
    /// Immutable constants belong to no instance: a literal, a <see langword="null"/>, and an enum
    /// member are compiled into the expression itself and are identical for every profile, so
    /// treating one as a capture would disable the cache for a selector that is perfectly safe to
    /// share.
    /// </remarks>
    [Fact]
    public void AConstantOfAnImmutableKind_IsNotACapture()
    {
        Assert.False(Captured<string>(x => x.Name));
        // string: a reference type, and the clause that exempts it by name.
        Assert.False(Captured<string>(x => x.Name + "suffix"));
        // Value types: an int literal and an enum member.
        Assert.False(Captured<int>(x => x.Count + 3));
        Assert.False(Captured<bool>(x => x.Day == DayOfWeek.Tuesday));
        Assert.False(Captured<bool>(x => x.Count > 0));
        // A null constant. The node is visited with a null Value, which must be skipped before
        // anything dereferences it.
        Assert.False(Captured<bool>(x => x.Parent == null));
        Assert.False(Captured<string>(x => x.Name ?? "n/a"));
    }

    /// <summary>
    /// Anything else non-null is a capture — the display class Roslyn builds for a captured local,
    /// and the declaring instance itself when the lambda reads a field.
    /// </summary>
    [Fact]
    public void AClosureOverAnythingElse_IsACapture()
    {
        string local = Outside();
        Assert.True(Captured<string>(x => x.Name + local));

        var dep = new Dep();
        Assert.True(Captured<string>(x => dep.Stamp(x.Name)));

        // A boxed value type held BY a closure still arrives as the display class, so the
        // value-type exemption above cannot be used to smuggle a capture through.
        int captured = local.Length;
        Assert.True(Captured<int>(x => x.Count + captured));
    }

    /// <summary>
    /// A static is read at invocation time and is per-process by construction, so it is not an
    /// instance capture and must not disable the cache.
    /// </summary>
    [Fact]
    public void AStaticRead_IsNotACapture() =>
        Assert.False(Captured<string>(x => x.Name + StaticStamp));

    private static string StaticStamp => "static";

    private static bool Captured<T>(Expression<Func<Row, T>> selector) =>
        CapturedState.IsCapturedByExpression(selector);
}
