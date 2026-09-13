using System;
using System.Linq.Expressions;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Direct coverage for <see cref="CapturedState.IsCapturedByExpression"/>'s constant judgment.
/// </summary>
/// <remarks>
/// #483 and #488 both turn on one line, and it was otherwise reachable only through the
/// profile/cache seam, which observes the CONSEQUENCE (is the compiled delegate shared) rather
/// than the judgment. Both directions are asserted because both cost: a false positive pays an
/// <c>Expression.Compile()</c> per request, a false negative is the stale scoped dependency the
/// mechanism exists to prevent.
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
        // A value-typed constant. (An enum comparison lifts to an Int32 constant in the
        // expression tree, so it is this same case rather than a distinct one.)
        Assert.False(Captured<int>(x => x.Count + 3));
        // A null constant. The node is visited with a null Value, which must be skipped before
        // anything dereferences it.
        Assert.False(Captured<bool>(x => x.Parent == null));
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

    // A static read was asserted here as "not a capture". Removed: a static member is a
    // MemberExpression with a null Expression, so it produces no ConstantExpression at all and
    // the probe could not have answered otherwise however line 127 is mutated. CLAUDE.md's
    // "a static field is read at INVOCATION time" is true of the C# compiler, not of a judgment
    // this code makes -- there is nothing here to pin.

    private static bool Captured<T>(Expression<Func<Row, T>> selector) =>
        CapturedState.IsCapturedByExpression(selector);
}
