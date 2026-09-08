using System;
using System.Linq.Expressions;
using System.Reflection;

namespace OhData;

// Stays in the core although delta PROFILES moved to the mapper package (#665): DeltaExtensions --
// the Delta<T> sugar, which the owner ruled stays here -- parses the same selector shape. One
// implementation with two consumers across the package boundary, rather than the second
// transcription this repository treats as a defect class in its own right.
/// <summary>
/// Shared expression-parsing helper for delta declarations. Accepts only a direct property access
/// on the lambda parameter (after stripping a boxing <c>Convert</c>); rejects method calls,
/// computed values, and nested access with a clear message.
/// </summary>
internal static class DeltaExpressionHelper
{
    internal static string GetMemberName(LambdaExpression expression, string argName)
    {
        Expression body = expression.Body;
        if (body is UnaryExpression unary &&
            (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
        {
            body = unary.Operand;
        }

        if (body is MemberExpression member &&
            member.Expression is ParameterExpression &&
            member.Member is PropertyInfo)
        {
            return member.Member.Name;
        }

        throw new ArgumentException(
            "Delta mapping selectors must be a direct property access on the lambda parameter " +
            "(e.g. x => x.Name). Method calls, computed values, and nested access such as " +
            "x => x.Category.Name are not supported.",
            argName);
    }
}
