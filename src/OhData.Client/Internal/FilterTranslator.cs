using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;

namespace OhData.Client.Internal;

/// <summary>
/// Translates a <see cref="Expression{TDelegate}"/> predicate into an OData 4.0
/// <c>$filter</c> expression string. Stateless — each call to <see cref="Translate{T}"/>
/// creates a fresh visitor instance.
/// </summary>
internal sealed class FilterTranslator : ExpressionVisitor
{
    private readonly StringBuilder _sb = new();
    private readonly ParameterExpression _parameter;
    private readonly JsonNamingPolicy? _namingPolicy;
    /// <summary>
    /// Prefix to emit for the parameter itself (empty for the root lambda parameter;
    /// set to the range-variable name for sub-translators used in any()/all() predicates).
    /// </summary>
    private readonly string _parameterPrefix;

    private FilterTranslator(ParameterExpression parameter) : this(parameter, null, "") { }

    private FilterTranslator(ParameterExpression parameter, JsonNamingPolicy? namingPolicy)
        : this(parameter, namingPolicy, "") { }

    private FilterTranslator(ParameterExpression parameter, JsonNamingPolicy? namingPolicy, string parameterPrefix)
    {
        _parameter = parameter;
        _namingPolicy = namingPolicy;
        _parameterPrefix = parameterPrefix;
    }

    /// <summary>Translates <paramref name="predicate"/> to an OData filter string.</summary>
    public static string Translate<T>(Expression<Func<T, bool>> predicate, JsonNamingPolicy? namingPolicy = null)
    {
        var t = new FilterTranslator(predicate.Parameters[0], namingPolicy);
        t.Visit(predicate.Body);
        return t._sb.ToString();
    }

    // ── Binary expressions ──────────────────────────────────────────────────────

    protected override Expression VisitBinary(BinaryExpression node)
    {
        var (op, parens) = node.NodeType switch
        {
            ExpressionType.Equal => ("eq", false),
            ExpressionType.NotEqual => ("ne", false),
            ExpressionType.GreaterThan => ("gt", false),
            ExpressionType.GreaterThanOrEqual => ("ge", false),
            ExpressionType.LessThan => ("lt", false),
            ExpressionType.LessThanOrEqual => ("le", false),
            ExpressionType.AndAlso => ("and", true),
            ExpressionType.OrElse => ("or", true),
            // Arithmetic: always parenthesize operands to avoid precedence ambiguity.
            // E.g. (A + B) * C must stay ((A) add (B)) mul (C) not "A add B mul C".
            ExpressionType.Add => ("add", true),
            ExpressionType.Subtract => ("sub", true),
            ExpressionType.Multiply => ("mul", true),
            ExpressionType.Divide => ("div", true),
            ExpressionType.Modulo => ("mod", true),
            _ => throw new NotSupportedException(
                $"Binary operator '{node.NodeType}' is not supported in OData $filter expressions.")
        };

        if (parens)
        {
            // Wrap each operand: (left) and/or (right)
            _sb.Append('('); Visit(node.Left); _sb.Append(')');
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
        // Handle Nullable<T>.HasValue → "Property ne null"
        if (node.Member.Name == "HasValue"
            && node.Expression is MemberExpression innerMember
            && innerMember.Type.IsGenericType
            && innerMember.Type.GetGenericTypeDefinition() == typeof(Nullable<>)
            && TryGetPropertyPath(innerMember, out string? hasValuePath))
        {
            _sb.Append(hasValuePath);
            _sb.Append(" ne null");
            return node;
        }

        // Handle Nullable<T>.Value → just the property path (strip .Value accessor)
        if (node.Member.Name == "Value"
            && node.Expression is MemberExpression innerMember2
            && innerMember2.Type.IsGenericType
            && innerMember2.Type.GetGenericTypeDefinition() == typeof(Nullable<>)
            && TryGetPropertyPath(innerMember2, out string? valuePath))
        {
            _sb.Append(valuePath);
            return node;
        }

        if (TryGetPropertyPath(node, out string? path))
        {
            _sb.Append(path);
        }
        else
        {
            // Captured variable or outer-scope field — evaluate at translation time
            object? value = Expression.Lambda<Func<object?>>(Expression.Convert(node, typeof(object))).Compile(preferInterpretation: true)();
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

    // ── Constructor calls ────────────────────────────────────────────────────────

    protected override Expression VisitNew(NewExpression node)
    {
        // Evaluate inline constructor calls (e.g. new DateOnly(2024, 1, 15)) at
        // translation time. This handles value types commonly used as filter operands.
        object? value = Expression.Lambda<Func<object?>>(Expression.Convert(node, typeof(object))).Compile(preferInterpretation: true)();
        _sb.Append(FormatLiteral(value));
        return node;
    }

    // ── Method calls ────────────────────────────────────────────────────────────

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // Static string.IsNullOrEmpty(x.Prop)
        if (node.Method.Name == "IsNullOrEmpty"
         && node.Method.IsStatic
         && node.Method.DeclaringType == typeof(string))
        {
            // Render the argument once; reuse the string to avoid double-compiling closures.
            int start = _sb.Length;
            Visit(node.Arguments[0]);
            string rendered = _sb.ToString(start, _sb.Length - start);
            _sb.Remove(start, _sb.Length - start);
            _sb.Append('(');
            _sb.Append(rendered);
            _sb.Append(" eq null or ");
            _sb.Append(rendered);
            _sb.Append(" eq '')");
            return node;
        }

        // Static string.IsNullOrWhiteSpace(x.Prop) — translates to (prop eq null or trim(prop) eq '')
        if (node.Method.Name == "IsNullOrWhiteSpace"
         && node.Method.IsStatic
         && node.Method.DeclaringType == typeof(string))
        {
            int start = _sb.Length;
            Visit(node.Arguments[0]);
            string rendered = _sb.ToString(start, _sb.Length - start);
            _sb.Remove(start, _sb.Length - start);
            _sb.Append('(');
            _sb.Append(rendered);
            _sb.Append(" eq null or trim(");
            _sb.Append(rendered);
            _sb.Append(") eq '')");
            return node;
        }

        if (node.Method.DeclaringType == typeof(string))
            return HandleStringMethod(node);

        // LINQ Any/All on collection properties → OData any()/all()
        if ((node.Method.Name == "Any" || node.Method.Name == "All")
            && node.Method.DeclaringType == typeof(Enumerable)
            && node.Arguments.Count == 2
            && node.Arguments[1] is LambdaExpression lambdaArg)
        {
            int start = _sb.Length;
            Visit(node.Arguments[0]);
            string propPath = _sb.ToString(start, _sb.Length - start);
            _sb.Remove(start, _sb.Length - start);

            string odataFunc = node.Method.Name == "Any" ? "any" : "all";
            string rangeVar = lambdaArg.Parameters[0].Name ?? "t";

            // Build a sub-translator with the range variable as its parameter.
            // The parameterPrefix ensures "t/Name" is emitted instead of just "Name".
            var subTranslator = new FilterTranslator(lambdaArg.Parameters[0], _namingPolicy, rangeVar);
            subTranslator.Visit(lambdaArg.Body);
            string predicate = subTranslator._sb.ToString();

            _sb.Append(propPath);
            _sb.Append('/');
            _sb.Append(odataFunc);
            _sb.Append('(');
            _sb.Append(rangeVar);
            _sb.Append(':');
            _sb.Append(' ');
            _sb.Append(predicate);
            _sb.Append(')');
            return node;
        }

        // LINQ Any with no predicate on collection properties — not yet supported
        if (node.Method.Name == "Any"
            && node.Method.DeclaringType == typeof(Enumerable)
            && node.Arguments.Count == 1)
        {
            throw new NotSupportedException(
                "Enumerable.Any() without a predicate is not supported in OData $filter expressions. " +
                "Use Any(t => ...) to supply a predicate.");
        }

        // Contains(collection, member) → OData "Member in (values)"
        // Handles Enumerable.Contains, MemoryExtensions.Contains (span arrays), and similar
        // static 2-arg Contains methods. Strategy: if the method is named "Contains" with 2 args
        // where one side is evaluatable as a collection and the other is a property path, emit "in".
        if (node.Method.Name == "Contains" && node.Arguments.Count == 2)
        {
            // Try Arguments[0] as collection, Arguments[1] as property path
            if (TryGetPropertyPath(node.Arguments[1], out string? _))
            {
                object? colVal = TryEvaluateAsObject(node.Arguments[0]);
                IEnumerable? col = colVal as IEnumerable;
                if (col != null)
                {
                    int start = _sb.Length;
                    Visit(node.Arguments[1]);
                    string prop = _sb.ToString(start, _sb.Length - start);
                    _sb.Remove(start, _sb.Length - start);

                    _sb.Append(prop);
                    _sb.Append(" in (");
                    bool first = true;
                    foreach (object? item in col)
                    {
                        if (!first) _sb.Append(',');
                        _sb.Append(FormatLiteral(item));
                        first = false;
                    }
                    _sb.Append(')');
                    return node;
                }
            }

            // Try Arguments[1] as collection, Arguments[0] as property path (reversed order)
            if (TryGetPropertyPath(node.Arguments[0], out string? _))
            {
                object? colVal = TryEvaluateAsObject(node.Arguments[1]);
                IEnumerable? col = colVal as IEnumerable;
                if (col != null)
                {
                    int start = _sb.Length;
                    Visit(node.Arguments[0]);
                    string prop = _sb.ToString(start, _sb.Length - start);
                    _sb.Remove(start, _sb.Length - start);

                    _sb.Append(prop);
                    _sb.Append(" in (");
                    bool first = true;
                    foreach (object? item in col)
                    {
                        if (!first) _sb.Append(',');
                        _sb.Append(FormatLiteral(item));
                        first = false;
                    }
                    _sb.Append(')');
                    return node;
                }
            }
        }

        // Instance .Contains() on a collection property with a captured value
        // e.g.: x.Tags.Contains("sale") where Tags is a collection property
        // Note: this is different from string.Contains which is already handled
        if (node.Method.Name == "Contains"
            && node.Arguments.Count == 1
            && node.Object is not null
            && node.Method.DeclaringType != typeof(string))
        {
            if (TryGetPropertyPath(node.Object, out string? listPath))
            {
                int start = _sb.Length;
                Visit(node.Arguments[0]);
                string valueLiteral = _sb.ToString(start, _sb.Length - start);
                _sb.Remove(start, _sb.Length - start);
                _sb.Append(listPath);
                _sb.Append("/any(t: t eq ");
                _sb.Append(valueLiteral);
                _sb.Append(')');
                return node;
            }
        }

        throw new NotSupportedException(
            $"Method '{node.Method.DeclaringType?.Name}.{node.Method.Name}' is not supported " +
            "in OData $filter expressions. Use a raw string filter for unsupported methods.");
    }

    private Expression HandleStringMethod(MethodCallExpression node)
    {
        switch (node.Method.Name)
        {
            case "Contains" when node.Arguments.Count == 2 && node.Arguments[1].Type == typeof(StringComparison):
            case "StartsWith" when node.Arguments.Count == 2 && node.Arguments[1].Type == typeof(StringComparison):
            case "EndsWith" when node.Arguments.Count == 2 && node.Arguments[1].Type == typeof(StringComparison):
                throw new NotSupportedException(
                    $"String.{node.Method.Name} with StringComparison is not supported in OData $filter expressions. " +
                    "OData is inherently case-sensitive. Use tolower()/toupper() for case-insensitive comparisons, " +
                    "or use the single-argument overload for an exact (case-sensitive) match.");

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

    // ── Collection evaluation ───────────────────────────────────────────────────

    /// <summary>
    /// Attempts to evaluate <paramref name="expr"/> as a captured value.
    /// Handles closure field accesses (the common case for captured variables) via reflection,
    /// stripping any <c>Convert</c> wrappers first. Returns <see langword="null"/> if the
    /// expression cannot be evaluated without executing a span-based conversion.
    /// </summary>
    private static object? TryEvaluateAsObject(Expression expr)
    {
        // Strip conversion wrappers (e.g. array-to-ReadOnlySpan implicit conversions).
        // These may appear as UnaryExpression (Convert) or MethodCallExpression (op_Implicit).
        // We only care about the underlying value (typically a captured collection).
        while (true)
        {
            if (expr is UnaryExpression u
                && u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            {
                expr = u.Operand;
                continue;
            }

            // Implicit conversion operator method call: op_Implicit(source)
            if (expr is MethodCallExpression mc
                && mc.Method.IsSpecialName
                && mc.Method.Name == "op_Implicit"
                && mc.Arguments.Count == 1)
            {
                expr = mc.Arguments[0];
                continue;
            }

            break;
        }

        // Constant expression (inline literal or boxed value)
        if (expr is ConstantExpression constExpr)
            return constExpr.Value;

        // Direct field/property access on a closure (most common captured-variable pattern)
        if (expr is MemberExpression memberExpr)
        {
            try
            {
                if (memberExpr.Expression is ConstantExpression ce)
                {
                    return memberExpr.Member switch
                    {
                        System.Reflection.FieldInfo fi => fi.GetValue(ce.Value),
                        System.Reflection.PropertyInfo pi => pi.GetValue(ce.Value),
                        _ => null
                    };
                }
                // Nested closure: recurse
                object? outerObj = TryEvaluateAsObject(memberExpr.Expression!);
                if (outerObj is not null)
                {
                    return memberExpr.Member switch
                    {
                        System.Reflection.FieldInfo fi => fi.GetValue(outerObj),
                        System.Reflection.PropertyInfo pi => pi.GetValue(outerObj),
                        _ => null
                    };
                }
            }
            catch { /* fall through */ }
        }

        // Last resort: compile and invoke with preferInterpretation, fall back to JIT
        try
        {
            return Expression.Lambda<Func<object?>>(
                Expression.Convert(expr, typeof(object))).Compile(preferInterpretation: true)();
        }
        catch
        {
            try
            {
                return Expression.Lambda<Func<object?>>(
                    Expression.Convert(expr, typeof(object))).Compile()();
            }
            catch { return null; }
        }
    }

    // ── Path extraction ─────────────────────────────────────────────────────────

    private bool TryGetPropertyPath(Expression expr, out string path)
    {
        if (expr is ParameterExpression p && p == _parameter)
        {
            // Return the parameter prefix (empty string for root, range-var name for sub-translators)
            path = _parameterPrefix;
            return true;
        }

        if (expr is MemberExpression member && TryGetPropertyPath(member.Expression!, out string? parent))
        {
            string memberName = _namingPolicy?.ConvertName(member.Member.Name) ?? member.Member.Name;
            path = parent.Length == 0
                ? memberName
                : $"{parent}/{memberName}";
            return true;
        }

        path = "";
        return false;
    }

    // ── Literal formatting ──────────────────────────────────────────────────────

    /// <summary>Formats a CLR value as an OData literal string (no surrounding whitespace).</summary>
    internal static string FormatLiteral(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => $"'{s.Replace("'", "''")}'",
        char c => $"'{c}'",
        Guid g => g.ToString(),
        DateTime dt => FormatDateTime(dt),
        DateTimeOffset dto => FormatDateTimeOffset(dto),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal dec => dec.ToString(CultureInfo.InvariantCulture),
        // All other numeric types (int, long, short, byte, sbyte, uint, ulong, ushort)
        _ when value.GetType().IsEnum
                        => Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture),
        _ => string.Format(CultureInfo.InvariantCulture, "{0}", value),
    };

    private static string FormatDateTime(DateTime dt)
    {
        // Always use the pattern WITHOUT "Z" so we can append the suffix after trimming.
        string s = dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        string suffix = dt.Kind == DateTimeKind.Utc ? "Z" : "";
        return TrimFractionalZeros(s, suffix);
    }

    private static string FormatDateTimeOffset(DateTimeOffset dto)
    {
        // Format date+time with fractional seconds (no offset/Z in pattern), then trim,
        // then append the correct suffix to avoid trimming "0" digits from the offset.
        string s = dto.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        string suffix = dto.Offset == TimeSpan.Zero
            ? "Z"
            : dto.ToString("zzz", CultureInfo.InvariantCulture);
        return TrimFractionalZeros(s, suffix);
    }

    private static string TrimFractionalZeros(string s, string suffix)
    {
        // Find the fractional seconds dot (search starts after "yyyy-MM-ddT" = 11 chars)
        int dotIdx = s.IndexOf('.', 10);
        if (dotIdx < 0) return s + suffix;
        // Trim trailing zeros from the fractional digits.
        int trimEnd = s.Length;
        while (trimEnd > dotIdx + 1 && s[trimEnd - 1] == '0') trimEnd--;
        if (trimEnd == dotIdx + 1) trimEnd = dotIdx; // all zeros — remove dot too
        return s[..trimEnd] + suffix;
    }
}
