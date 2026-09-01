using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace OhData;

/// <summary>
/// Detects whether a user-supplied selector expression or converter delegate closes over state
/// belonging to the instance that declared it.
/// </summary>
/// <remarks>
/// <para>
/// Two subsystems freeze user lambdas past the lifetime of the object that supplied them, and both
/// shipped a silent stale/disposed-dependency bug because of it:
/// </para>
/// <list type="bullet">
/// <item><description>
/// #483 — <c>EntitySetProfile</c>'s <c>s_etagCache</c>/<c>s_keyToStringCache</c>/<c>s_keyToUrlCache</c>
/// are keyed by <c>GetType()</c> and store a delegate compiled from the FIRST-constructed instance's
/// expressions. That instance comes from the startup scope, which is disposed immediately after
/// registration.
/// </description></item>
/// <item><description>
/// #488 item 1 — <c>DeltaMapping.Convert</c>'s converter is hoisted verbatim into the
/// process-lifetime singleton <c>DeltaMappingPlan</c>, built from a profile resolved in a scope
/// <c>DeltaFactory.Build</c> disposes on the next line.
/// </description></item>
/// </list>
/// <para>
/// Profiles of both kinds are registered <c>AddScoped</c> precisely so they can inject scoped
/// services, so the framework invites the constructor shape that makes such a lambda natural to
/// write. The two subsystems answer differently — the caches simply stop caching (nothing is lost;
/// the fix is invisible to a non-capturing selector), while a delta converter cannot be recompiled
/// per request and is therefore refused at declaration — but the question they ask is the same one,
/// so it is asked in one place.
/// </para>
/// </remarks>
internal static class CapturedState
{
    /// <summary>
    /// True when <paramref name="expression"/> reads anything held by a closure — a captured local,
    /// parameter, or (via <c>this</c>) a field of the declaring instance.
    /// </summary>
    /// <remarks>
    /// C# compiles every such capture into a <see cref="ConstantExpression"/> holding the display
    /// class (or the declaring instance itself when only <c>this</c> is captured), with the capture
    /// read off it as a member access. A constant is the only node whose value is frozen when the
    /// lambda is compiled: a static field or property is read at INVOCATION time, so it is shared
    /// per-process by construction and not a per-instance capture at all.
    /// <para>
    /// Value-typed and <see cref="string"/> constants — a literal <c>3</c>, <c>"x"</c>, an enum such
    /// as <c>StringComparison.Ordinal</c> — are immutable and belong to no instance, so they are not
    /// captures. Anything else non-null is treated as one. Both callers are safe against a false
    /// positive here (one loses a cache entry, the other reports a fixable declaration error), so
    /// the judgment deliberately errs toward "captured".
    /// </para>
    /// </remarks>
    /// <param name="expression">The selector lambda supplied by the profile.</param>
    internal static bool IsCapturedByExpression(Expression expression)
    {
        var probe = new ConstantProbe();
        probe.Visit(expression);
        return probe.Found;
    }

    /// <summary>
    /// True when <paramref name="handler"/> carries captured state: a closure display class, or an
    /// instance method group bound to a receiver.
    /// </summary>
    /// <remarks>
    /// Measured on .NET 10.0.11, and the two conditions are not interchangeable:
    /// <list type="bullet">
    /// <item><description>
    /// a static method group has a <b>null</b> target — nothing is captured;
    /// </description></item>
    /// <item><description>
    /// a non-capturing lambda, <c>static</c> or not, compiles to an instance method on Roslyn's
    /// cached <c>&lt;&gt;c</c> singleton: <c>[CompilerGenerated]</c>, <b>zero</b> instance fields —
    /// nothing is captured either, and it is shared process-wide;
    /// </description></item>
    /// <item><description>
    /// a capturing lambda compiles to a <c>&lt;&gt;c__DisplayClass</c>: <c>[CompilerGenerated]</c>
    /// with a field per capture;
    /// </description></item>
    /// <item><description>
    /// an INSTANCE method group (<c>_dep.Convert</c>) binds the receiver itself as the target, and
    /// that type is not compiler-generated. Asking only "does the target have instance fields"
    /// would miss a receiver that happens to declare none — measured, a field-less scoped service
    /// used as a method group reports zero — which is why the compiler-generated distinction is
    /// consulted rather than the field count alone.
    /// </description></item>
    /// </list>
    /// So: a non-compiler-generated target is a captured receiver outright; a compiler-generated
    /// one is captured only when it actually holds something. Neither test matches a
    /// compiler-generated type by NAME. Every entry in the invocation list is examined, so a
    /// multicast delegate cannot hide a capturing member behind a non-capturing one.
    /// </remarks>
    /// <param name="handler">The converter delegate supplied by the profile.</param>
    internal static bool IsCapturedByDelegate(Delegate handler)
    {
        foreach (Delegate part in handler.GetInvocationList())
        {
            object? target = part.Target;
            if (target is null) continue;   // static method: no receiver, no capture

            Type targetType = target.GetType();
            if (targetType.GetCustomAttribute<CompilerGeneratedAttribute>() is null) return true;

            if (targetType.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ConstantProbe : ExpressionVisitor
    {
        internal bool Found { get; private set; }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            object? value = node.Value;
            if (value is not null && !value.GetType().IsValueType && value is not string) Found = true;
            return node;
        }
    }
}
