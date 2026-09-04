using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #641 — the handler delegates' nullability states each one's <c>null</c> contract.
/// <para>
/// <c>GetById</c>, <c>Put</c> and <c>Patch</c> return <c>OhDataResult&lt;TModel?&gt;</c> because a
/// null value is a legitimate outcome there: <c>404</c> for all three, or an upsert on <c>Put</c>
/// when <c>AllowUpsert</c> is set. <c>Post</c> returns <c>OhDataResult&lt;TModel&gt;</c> because a
/// null from it is a <b>server-side contract violation</b> and answers <c>500</c> (#496 finding 1).
/// </para>
/// <para>
/// So the annotation is not decoration and not redundant with <c>OhDataResult&lt;T&gt;.Value</c>
/// being <c>T?</c> — it is the one place the difference between "absent is expected here" and
/// "absent is a bug here" is visible without reading the framework. This test exists so that
/// distinction cannot be flattened by someone making the four consistent.
/// </para>
/// </summary>
public sealed class Issue641HandlerNullabilityTests
{
    private static FieldInfo Handler(string name) =>
        typeof(EntitySetProfile<int, Widget>)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"no handler field named '{name}'");

    // The delegate's LAST type argument is its return type, Task<OhDataResult<T>>; unwrap to T.
    private static Type ResultValueType(string handlerName)
    {
        Type[] args = Handler(handlerName).FieldType.GetGenericArguments();
        Type task = args[^1];                                   // Task<OhDataResult<T>>
        Type result = task.GetGenericArguments()[0];            // OhDataResult<T>
        return result.GetGenericArguments()[0];                 // T
    }

    [Theory]
    [InlineData("GetById")]
    [InlineData("Put")]
    [InlineData("Patch")]
    [InlineData("Post")]
    public void EveryEntityHandlerCarriesTheModelType(string handler)
    {
        // Runtime types cannot distinguish Widget from Widget? -- nullable REFERENCE annotations are
        // metadata, not type identity. So this asserts the shape, and the annotation itself is
        // asserted below from the attribute the compiler emits.
        Assert.Equal(typeof(Widget), ResultValueType(handler));
    }

    [Theory]
    [InlineData("GetById", true)]    // null -> 404
    [InlineData("Put", true)]        // null -> 404, or an upsert when AllowUpsert is set
    [InlineData("Patch", true)]      // null -> 404
    [InlineData("Post", false)]      // null -> 500: the handler broke its contract (#496 finding 1)
    public void TheNullabilityAnnotationMatchesTheHandlersNullContract(string handler, bool nullExpected)
    {
        // NullableAttribute's byte flags walk the field's generic type tree in order, so the LAST
        // one is the innermost reference type -- the OhDataResult<T> argument. Measured layout:
        //
        //   GetById [2,1,1,1,2]     Put [2,1,1,1,1,2]     Patch [2,1,1,1,1,1,2]     Post [2,1,1,1,1]
        //
        // "any flag is 2" was the first attempt and is WRONG: flag[0] is the FIELD, and every
        // handler is declared `Func<...>? = null`, so all four carry a leading 2. It reported Post
        // as nullable. Value-type parameters (TKey=int, CancellationToken) contribute no flag, which
        // is why the lengths differ and why an index counted from the front would not survive a
        // delegate gaining a parameter.
        CustomAttributeData? nullable = Handler(handler).CustomAttributes
            .FirstOrDefault(a => a.AttributeType.Name == "NullableAttribute");

        Assert.NotNull(nullable);

        // `as` + null-forgiving asserted the shape twice and told the compiler nothing;
        // IsAssignableFrom both fails the test on the wrong shape AND hands back a non-null
        // reference, so nothing below is suppressed.
        IReadOnlyList<CustomAttributeTypedArgument> flags =
            Assert.IsAssignableFrom<IReadOnlyList<CustomAttributeTypedArgument>>(
                nullable.ConstructorArguments[0].Value);

        object? lastFlag = flags[^1].Value;
        Assert.NotNull(lastFlag);

        bool modelIsNullable = (byte)lastFlag == 2;
        Assert.True(modelIsNullable == nullExpected,
            $"'{handler}' should {(nullExpected ? "" : "NOT ")}declare a nullable model type. " +
            $"Flags: [{string.Join(",", flags.Select(f => f.Value))}]");
    }
}
