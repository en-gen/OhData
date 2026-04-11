using System;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;

namespace OhData.Client.Internal;

/// <summary>
/// Translates a <see cref="Expression{TDelegate}"/> predicate into an OData 4.0
/// <c>$filter</c> expression string. Stateless — each call to <see cref="Translate{T}"/>
/// creates a fresh visitor instance.
/// </summary>
internal sealed class FilterTranslator : ExpressionVisitor
{
    private readonly StringBuilder       _sb        = new();
    private readonly ParameterExpression _parameter;

    private FilterTranslator(ParameterExpression parameter) => _parameter = parameter;

    /// <summary>Translates <paramref name="predicate"/> to an OData filter string.</summary>
    public static string Translate<T>(Expression<Func<T, bool>> predicate)
    {
        var t = new FilterTranslator(predicate.Parameters[0]);
        t.Visit(predicate.Body);
        return t._sb.ToString();
    }

    // ── Binary expressions ──────────────────────────────────────────────────────

    protected override Expression VisitBinary(BinaryExpression node)
    {
        var (op, parens) = node.NodeType switch
        {
            ExpressionType.Equal              => ("eq",  false),
            ExpressionType.NotEqual           => ("ne",  false),
            ExpressionType.GreaterThan        => ("gt",  false),
            ExpressionType.GreaterThanOrEqual => ("ge",  false),
            ExpressionType.LessThan           => ("lt",  false),
            ExpressionType.LessThanOrEqual    => ("le",  false),
            ExpressionType.AndAlso            => ("and", true),
            ExpressionType.OrElse             => ("or",  true),
            ExpressionType.Add                => ("add", false),
            ExpressionType.Subtract           => ("sub", false),
            ExpressionType.Multiply           => ("mul", false),
            ExpressionType.Divide             => ("div", false),
            ExpressionType.Modulo             => ("mod", false),
            _ => throw new NotSupportedException(
                $"Binary operator '{node.NodeType}' is not supported in OData $filter expressions.")
        };

        if (parens)
        {
            // Wrap each operand: (left) and/or (right)
            _sb.Append('('); Visit(node.Left);  _sb.Append(')');
            _sb.Append($" {op} ");
            _sb.Append('('); Visit(node.Right); _sb.Append(')');
        }
        else
        {
            Visit(node.Left);
            _sb.Append($" {op} ");
            Visit(node.Right);
        }
        return node;
    }

    // ── Unary expressions ───────────────────────────────────────────────────────

    protected override Expression VisitUnary(UnaryExpression node)
    {
        switch (node.NodeType)
        {
            case ExpressionType.Not:
                _sb.Append("not (");
                Visit(node.Operand);
                _sb.Append(')');
                return node;

            // Boxing conversions appear when lambdas are typed as Func<T, object>
            case ExpressionType.Convert:
            case ExpressionType.ConvertChecked:
            case ExpressionType.TypeAs:
                Visit(node.Operand);
                return node;

            default:
                throw new NotSupportedException(
                    $"Unary operator '{node.NodeType}' is not supported in OData $filter expressions.");
        }
    }

    // ── Member access ───────────────────────────────────────────────────────────

    protected override Expression VisitMember(MemberExpression node)
    {
        if (TryGetPropertyPath(node, out var path))
        {
            _sb.Append(path);
        }
        else
        {
            // Captured variable or outer-scope field — evaluate at translation time
            var value = Expression.Lambda<Func<object?>>(Expression.Convert(node, typeof(object))).Compile()();
            _sb.Append(FormatLiteral(value));
        }
        return node;
    }

    // ── Constants ───────────────────────────────────────────────────────────────

    protected override Expression VisitConstant(ConstantExpression node)
    {
        _sb.Append(FormatLiteral(node.Value));
        return node;
    }

    // ── Method calls ────────────────────────────────────────────────────────────

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // Static string.IsNullOrEmpty(x.Prop)
        if (node.Method.Name     == "IsNullOrEmpty"
         && node.Method.IsStatic
         && node.Method.DeclaringType == typeof(string))
        {
            _sb.Append('(');
            Visit(node.Arguments[0]);
            _sb.Append(" eq null or ");
            Visit(node.Arguments[0]);
            _sb.Append(" eq '')");
            return node;
        }

        if (node.Method.DeclaringType == typeof(string))
            return HandleStringMethod(node);

        throw new NotSupportedException(
            $"Method '{node.Method.DeclaringType?.Name}.{node.Method.Name}' is not supported " +
            "in OData $filter expressions. Use a raw string filter for unsupported methods.");
    }

    private Expression HandleStringMethod(MethodCallExpression node)
    {
        switch (node.Method.Name)
        {
            case "Contains" when node.Arguments.Count == 1:
                EmitFunction("contains", node.Object!, node.Arguments[0]); return node;

            case "StartsWith" when node.Arguments.Count == 1:
                EmitFunction("startswith", node.Object!, node.Arguments[0]); return node;

            case "EndsWith" when node.Arguments.Count == 1:
                EmitFunction("endswith", node.Object!, node.Arguments[0]); return node;

            case "ToLower" or "ToLowerInvariant":
                _sb.Append("tolower("); Visit(node.Object!); _sb.Append(')'); return node;

            case "ToUpper" or "ToUpperInvariant":
                _sb.Append("toupper("); Visit(node.Object!); _sb.Append(')'); return node;

            case "Trim":
                _sb.Append("trim("); Visit(node.Object!); _sb.Append(')'); return node;

            case "Concat" when node.Arguments.Count == 2:
                EmitFunction("concat", node.Arguments[0], node.Arguments[1]); return node;

            default:
                throw new NotSupportedException(
                    $"String method '{node.Method.Name}' is not supported in OData $filter expressions.");
        }
    }

    private void EmitFunction(string name, Expression a, Expression b)
    {
        _sb.Append(name); _sb.Append('(');
        Visit(a);
        _sb.Append(',');
        Visit(b);
        _sb.Append(')');
    }

    // ── Path extraction ─────────────────────────────────────────────────────────

    private bool TryGetPropertyPath(Expression expr, out string path)
    {
        if (expr is ParameterExpression p && p == _parameter)
        {
            path = "";
            return true;
        }

        if (expr is MemberExpression member && TryGetPropertyPath(member.Expression!, out var parent))
        {
            path = parent.Length == 0
                ? member.Member.Name
                : $"{parent}/{member.Member.Name}";
            return true;
        }

        path = "";
        return false;
    }

    // ── Literal formatting ──────────────────────────────────────────────────────

    /// <summary>Formats a CLR value as an OData literal string (no surrounding whitespace).</summary>
    internal static string FormatLiteral(object? value) => value switch
    {
        null            => "null",
        bool b          => b ? "true" : "false",
        string s        => $"'{s.Replace("'", "''")}'",
        char c          => $"'{c}'",
        Guid g          => g.ToString(),
        DateTime dt     => dt.Kind == DateTimeKind.Utc
                               ? $"'{dt:yyyy-MM-ddTHH:mm:ssZ}'"
                               : $"'{dt:yyyy-MM-ddTHH:mm:ss}'",
        DateTimeOffset dto => dto.Offset == TimeSpan.Zero
                               ? $"'{dto:yyyy-MM-ddTHH:mm:ssZ}'"
                               : $"'{dto:yyyy-MM-ddTHH:mm:sszzz}'",
        DateOnly d      => $"'{d:yyyy-MM-dd}'",
        TimeOnly t      => $"'{t:HH:mm:ss}'",
        float f         => f.ToString("R", CultureInfo.InvariantCulture),
        double d        => d.ToString("R", CultureInfo.InvariantCulture),
        decimal dec     => dec.ToString(CultureInfo.InvariantCulture),
        // All other numeric types (int, long, short, byte, sbyte, uint, ulong, ushort)
        _ when value.GetType().IsEnum
                        => Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture),
        _               => string.Format(CultureInfo.InvariantCulture, "{0}", value),
    };
}
