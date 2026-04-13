using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;

namespace OhData.Client.Internal;

/// <summary>
/// Translates a projection expression to an OData <c>$select</c> clause string.
/// Supports single-property access (<c>x =&gt; x.Name</c>) and anonymous-type projections
/// (<c>x =&gt; new { x.Name, x.Price }</c>), including navigation paths
/// (<c>x =&gt; x.Category.Name</c> → <c>Category/Name</c>).
/// </summary>
internal static class SelectTranslator
{
    public static string Translate<T>(Expression<Func<T, object?>> selector, JsonNamingPolicy? namingPolicy = null)
    {
        var body = StripConvert(selector.Body);
        var param = selector.Parameters[0];

        // x => new { x.Name, x.Price }  or  x => new { x.Name, Code = x.CategoryCode }
        if (body is NewExpression newExpr)
            return string.Join(',', newExpr.Arguments.Select(a => ExtractPath(a, param, namingPolicy)));

        // x => new Dto { Name = x.Name }
        if (body is MemberInitExpression memberInit)
        {
            return string.Join(',', memberInit.Bindings
                .OfType<MemberAssignment>()
                .Select(b => ExtractPath(b.Expression, param, namingPolicy)));
        }

        // x => x.Name  (single property)
        return ExtractPath(body, param, namingPolicy);
    }

    private static string ExtractPath(Expression expr, ParameterExpression param, JsonNamingPolicy? namingPolicy)
    {
        expr = StripConvert(expr);

        if (expr is MemberExpression member)
        {
            string memberName = namingPolicy?.ConvertName(member.Member.Name) ?? member.Member.Name;

            if (member.Expression is ParameterExpression p && p == param)
                return memberName;

            if (member.Expression is not null)
                return $"{ExtractPath(member.Expression, param, namingPolicy)}/{memberName}";
        }

        throw new ArgumentException(
            $"Select argument '{expr}' is not a supported property access. " +
            "Only property paths (x => x.Name, x => x.Category.Name) are valid in $select.");
    }

    private static Expression StripConvert(Expression expr)
    {
        while (expr is UnaryExpression u
            && u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            expr = u.Operand;
        }

        return expr;
    }
}
