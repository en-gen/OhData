using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using OhData.Client.Internal;
using Xunit;

namespace OhData.Client.Tests;

/// <summary>
/// #459. <c>FilterTranslator.TryEvaluateAsObject</c> used to answer <c>null</c> for two different
/// conditions — "the value is null" and "evaluating it threw" — because every reflection and
/// compile/invoke path ended in <c>catch { return null; }</c>. <c>VisitMember</c>'s fallback then
/// emitted <c>FormatLiteral(null)</c>, so <c>Filter(x =&gt; x.Name == src.Bad)</c> with a throwing
/// getter became <c>Name eq null</c>: a query the caller never wrote, executed against the server,
/// returning rows where <c>Name</c> is null, with no exception anywhere.
/// <para>
/// The two conditions are now separated, so every test below pins one side of that separation. They
/// have to be asserted as a pair: a fix that throws on <em>every</em> unevaluatable member would
/// also break the genuine-null case, and a fix that emits <c>null</c> for everything is the bug.
/// </para>
/// <para>
/// The pattern being applied is the file's own, twenty lines above the defect: the
/// range-variable case was already fixed to fail loudly through <c>ContainsParameterReference</c>,
/// and its comment calls the swallowed-throw behaviour "the original bug".
/// </para>
/// </summary>
public class CapturedValueEvaluationTests
{
    private sealed class Item
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<Tag> Tags { get; set; } = new();
    }

    private sealed class Tag
    {
        public string Name { get; set; } = "";
    }

    private sealed class Source
    {
        /// <summary>A getter that throws — the #459 repro.</summary>
        public string Bad => throw new InvalidOperationException("getter blew up");

        /// <summary>A getter that genuinely returns null — must stay <c>eq null</c>.</summary>
        public string? Absent => null;

        public string Good => "ok";

        /// <summary>A collection-valued getter that throws, for the <c>in</c>-operator probe.</summary>
        public string[] BadNames => throw new InvalidOperationException("collection getter blew up");

        public string[] GoodNames => new[] { "a", "b" };

        public Nested? Inner { get; set; }
    }

    private sealed class Nested
    {
        public string Bad => throw new InvalidOperationException("nested getter blew up");
        public string Good => "nested-ok";
    }

    private static string F(Expression<Func<Item, bool>> expr) =>
        FilterTranslator.Translate(expr, null);

    // ── Condition 1: evaluation threw → fail loudly, never emit a literal ───────

    [Fact]
    public void ThrowingGetter_OnCapturedValue_Throws()
    {
        var src = new Source();

        var ex = Assert.Throws<NotSupportedException>(() => F(x => x.Name == src.Bad));

        // The one assertion that actually distinguishes fixed from broken: nothing about the
        // failed evaluation may reach the emitted filter.
        Assert.DoesNotContain("eq null", ex.Message, StringComparison.Ordinal);
        Assert.Contains("raw string $filter", ex.Message, StringComparison.Ordinal);

        // The cause is preserved and unwrapped from reflection's TargetInvocationException, so the
        // caller can see which getter failed rather than a bare "target of an invocation".
        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal("getter blew up", inner.Message);
        Assert.Contains("getter blew up", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowingGetter_OnNestedCapturedValue_Throws()
    {
        // The instance resolves fine; the leaf getter is what throws. This walks the recursive
        // branch of the evaluator rather than the single-hop closure-field branch.
        var src = new Source { Inner = new Nested() };

        var ex = Assert.Throws<NotSupportedException>(() => F(x => x.Name == src.Inner!.Bad));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("nested getter blew up", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowingGetter_InsideAnyPredicate_Throws()
    {
        // Sub-translators built for any()/all() share the evaluator, so the hole was reachable
        // from inside a nested predicate too.
        var src = new Source();

        Assert.Throws<NotSupportedException>(() => F(x => x.Tags.Any(t => t.Name == src.Bad)));
    }

    [Fact]
    public void ThrowingCollectionGetter_InContains_Throws()
    {
        // The `in`-operator path evaluates its collection argument through the same helper. A
        // throw there used to be swallowed into "not a collection", which then fell through to a
        // misleading "method not supported" diagnostic.
        var src = new Source();

        var ex = Assert.Throws<NotSupportedException>(() => F(x => src.BadNames.Contains(x.Name)));
        Assert.Contains("collection getter blew up", ex.Message, StringComparison.Ordinal);
    }

    // ── Condition 2: the value genuinely IS null → still emits `eq null` ────────

    [Fact]
    public void NullCapturedLocal_StillEmitsEqNull()
    {
        string? nothing = null;

        Assert.Equal("Name eq null", F(x => x.Name == nothing));
    }

    [Fact]
    public void NullReturningGetter_StillEmitsEqNull()
    {
        var src = new Source();

        Assert.Equal("Name eq null", F(x => x.Name == src.Absent));
    }

    [Fact]
    public void NullCapturedReference_OnNotEqual_StillEmitsNeNull()
    {
        string? nothing = null;

        Assert.Equal("Name ne null", F(x => x.Name != nothing));
    }

    // ── Neither condition: ordinary captured values are unaffected ──────────────

    [Fact]
    public void WorkingGetter_EmitsItsValue()
    {
        var src = new Source();

        Assert.Equal("Name eq 'ok'", F(x => x.Name == src.Good));
    }

    [Fact]
    public void WorkingNestedGetter_EmitsItsValue()
    {
        var src = new Source { Inner = new Nested() };

        Assert.Equal("Name eq 'nested-ok'", F(x => x.Name == src.Inner!.Good));
    }

    [Fact]
    public void WorkingCollectionGetter_StillEmitsInOperator()
    {
        var src = new Source();

        Assert.Equal("Name in ('a','b')", F(x => src.GoodNames.Contains(x.Name)));
    }

    // ── The third outcome: "no evaluation attempted" must NOT become a failure ──

    [Fact]
    public void RangeVariableArgumentToContains_KeepsItsOriginalDiagnostic()
    {
        // Contains(x.Tags, x.Name): both probes read a lambda range variable, which has no value at
        // translation time. That is not an evaluation failure, so it must keep falling through to
        // the pre-existing "method is not supported" message rather than being reported as a
        // captured value that blew up.
        var ex = Assert.Throws<NotSupportedException>(
            () => F(x => Enumerable.Contains(x.Tags.Select(t => t.Name), x.Name)));

        Assert.DoesNotContain("captured value", ex.Message, StringComparison.Ordinal);
    }
}
