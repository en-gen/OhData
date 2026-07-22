using System;
using System.Collections;
using System.Collections.Generic;
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
    /// <summary>
    /// One entry per enclosing any()/all() scope, outermost first. Lets a nested sub-translator
    /// resolve a reference to an ancestor lambda's range variable (or the root parameter) back to
    /// its OData path prefix, even when the reference is several any()/all() levels removed.
    /// </summary>
    private readonly struct OuterScope
    {
        public OuterScope(ParameterExpression parameter, string pathPrefix)
        {
            Parameter = parameter;
            PathPrefix = pathPrefix;
        }

        public ParameterExpression Parameter { get; }
        public string PathPrefix { get; }
    }

    private readonly StringBuilder _sb = new();
    private readonly ParameterExpression _parameter;
    private readonly JsonNamingPolicy? _namingPolicy;
    /// <summary>
    /// Prefix to emit for the parameter itself (empty for the root lambda parameter;
    /// set to the range-variable name for sub-translators used in any()/all() predicates).
    /// </summary>
    private readonly string _parameterPrefix;
    private readonly IReadOnlyList<OuterScope> _outerScopes;

    private FilterTranslator(ParameterExpression parameter, JsonNamingPolicy? namingPolicy)
        : this(parameter, namingPolicy, "", []) { }

    private FilterTranslator(
        ParameterExpression parameter,
        JsonNamingPolicy? namingPolicy,
        string parameterPrefix,
        IReadOnlyList<OuterScope> outerScopes)
    {
        _parameter = parameter;
        _namingPolicy = namingPolicy;
        _parameterPrefix = parameterPrefix;
        _outerScopes = outerScopes;
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
            // Detect enum comparisons where the C# compiler emits an integer constant
            // for the enum literal (e.g. x.Status == MyEnum.Active → Status eq 1).
            // Re-encode the integer side as an enum member name so OData servers that
            // declare enum properties receive 'Active' rather than 1.
            //
            // The C# compiler may represent enum comparisons in several ways:
            //   Equal(Status, Constant(1, int32))            ← plain int constant
            //   Equal(Status, Constant(Active, ItemStatus))  ← enum constant (handled by FormatLiteral)
            //   Equal(Status, Convert(Constant(1), ItemStatus)) ← Convert-wrapped int to enum
            //
            // We detect "one side is or resolves to an enum type, the other is a non-enum
            // integer constant" and emit the member name.
            Type? leftEnum = GetEnumType(node.Left);
            Type? rightEnum = GetEnumType(node.Right);
            object? rightEnumIntValue = TryGetNonEnumConstantValue(node.Right);
            object? leftEnumIntValue = TryGetNonEnumConstantValue(node.Left);

            if (leftEnum is not null && rightEnumIntValue is not null)
            {
                VisitOperand(node.Left);
                _sb.Append($" {op} ");
                _sb.Append(FormatLiteral(Enum.ToObject(leftEnum, rightEnumIntValue)));
            }
            else if (rightEnum is not null && leftEnumIntValue is not null)
            {
                _sb.Append(FormatLiteral(Enum.ToObject(rightEnum, leftEnumIntValue)));
                _sb.Append($" {op} ");
                VisitOperand(node.Right);
            }
            else
            {
                VisitOperand(node.Left);
                _sb.Append($" {op} ");
                VisitOperand(node.Right);
            }
        }
        return node;
    }

    /// <summary>
    /// Visits a non-arithmetic comparison operand (eq/ne/gt/ge/lt/le), wrapping it in an
    /// explicit extra pair of parens when it is itself a logical <c>and</c>/<c>or</c>
    /// expression. Without this, an operand tree like <c>b == (x || y)</c> — where the
    /// LINQ tree explicitly groups <c>x || y</c> as a single operand of <c>==</c> — would
    /// emit <c>b eq (x) or (y)</c>: because <c>eq</c> binds tighter than <c>or</c> in OData
    /// (Part 2 §5.1.1.1), a server parses that as <c>(b eq (x)) or (y)</c> — silently wrong.
    /// Arithmetic operands (add/sub/mul/div/mod) do not need this: they already bind tighter
    /// than every comparison operator, so no extra wrap changes their grouping.
    /// </summary>
    private void VisitOperand(Expression expr)
    {
        bool needsWrap = expr is BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse };
        if (needsWrap) _sb.Append('(');
        Visit(expr);
        if (needsWrap) _sb.Append(')');
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
                // If the convert is TO an enum type, format the converted value as an enum
                // member name. C# expression trees may emit Convert(enumType, intConstant)
                // for enum literal comparisons (e.g. x.Status == MyEnum.Active).
                if (node.Type.IsEnum && node.Operand is ConstantExpression enumConstant)
                {
                    object enumVal = Enum.ToObject(node.Type, enumConstant.Value!);
                    _sb.Append(FormatLiteral(enumVal));
                    return node;
                }
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

        // Date/time component accessors and string.Length → OData canonical functions
        // (year()/month()/day()/hour()/minute()/second()/length()), not nested property paths.
        // Without this, x.CreatedAt.Year emits "CreatedAt/Year" (a nonexistent navigation
        // path) instead of "year(CreatedAt)". The cheap member-name/type check runs first so
        // ordinary member accesses skip the path extraction entirely (perf-sensitive path).
        if (node.Expression is not null
            && TryGetTemporalFunctionName(node.Member.Name, node.Expression.Type, out string? functionName)
            && TryGetPropertyPath(node.Expression, out string? componentBasePath))
        {
            _sb.Append(functionName);
            _sb.Append('(');
            _sb.Append(componentBasePath);
            _sb.Append(')');
            return node;
        }

        if (TryGetPropertyPath(node, out string? path))
        {
            _sb.Append(path);
        }
        else if (ContainsParameterReference(node))
        {
            // The expression depends on a lambda range variable (this translator's own
            // parameter or an outer any()/all() scope's) but isn't a plain property-access
            // chain, so it cannot become an OData path — e.g. a member access on a ternary
            // that itself reads an outer range variable. Silently falling back to a captured-
            // value evaluation here would either throw-and-swallow to a bogus "null" literal
            // (the original bug) or evaluate stale/wrong data. Fail loudly instead.
            throw new NotSupportedException(
                $"The expression '{node}' references a lambda range variable in a way that " +
                "cannot be translated to an OData property path (for example, a member access " +
                "on a conditional/computed expression). Rewrite the predicate to compare a " +
                "direct property path, or supply a raw string $filter.");
        }
        else
        {
            // Captured variable or outer-scope field — evaluate at translation time.
            // TryEvaluateAsObject resolves the common closure field/property-access case via
            // direct reflection (no Expression.Compile() at all); it only falls back to
            // compiling an interpreted lambda for expressions reflection can't walk.
            object? value = TryEvaluateAsObject(node);
            _sb.Append(FormatLiteral(value));
        }
        return node;
    }

    // ── Parameter access (bare range variable, e.g. `t.Owner == x`) ────────────────

    protected override Expression VisitParameter(ParameterExpression node)
    {
        if (TryGetPropertyPath(node, out string? path))
        {
            _sb.Append(path);
            return node;
        }

        throw new NotSupportedException(
            $"Reference to range variable '{node.Name}' could not be translated to an OData " +
            "path. This typically means an entire entity/complex value is being compared " +
            "rather than one of its properties, which OData $filter cannot express directly.");
    }

    /// <summary>
    /// Returns the OData canonical temporal/string function name for a supported CLR
    /// component-accessor member (<c>Year</c>/<c>Month</c>/<c>Day</c>/<c>Hour</c>/
    /// <c>Minute</c>/<c>Second</c> on date/time types, <c>Length</c> on <see cref="string"/>).
    /// </summary>
    private static bool TryGetTemporalFunctionName(string memberName, Type declaringExpressionType, out string? functionName)
    {
        if (declaringExpressionType == typeof(string) && memberName == "Length")
        {
            functionName = "length";
            return true;
        }

        bool isDateLike = declaringExpressionType == typeof(DateTime)
            || declaringExpressionType == typeof(DateTimeOffset)
            || declaringExpressionType == typeof(DateOnly);
        bool isTimeLike = declaringExpressionType == typeof(DateTime)
            || declaringExpressionType == typeof(DateTimeOffset)
            || declaringExpressionType == typeof(TimeOnly);

        functionName = memberName switch
        {
            "Year" when isDateLike => "year",
            "Month" when isDateLike => "month",
            "Day" when isDateLike => "day",
            "Hour" when isTimeLike => "hour",
            "Minute" when isTimeLike => "minute",
            "Second" when isTimeLike => "second",
            _ => null,
        };
        return functionName is not null;
    }

    /// <summary>Returns <see langword="true"/> if <paramref name="expr"/> contains a reference to any lambda parameter.</summary>
    private static bool ContainsParameterReference(Expression expr)
    {
        // Fast path for the overwhelmingly common shape reaching this check: a plain
        // member-access chain. A chain bottoming out at a ConstantExpression is a captured
        // closure variable (no parameter); one bottoming out at a ParameterExpression is a
        // range-variable reference. Only fall back to the allocating visitor walk for
        // anything more exotic (method calls, conditionals, ...).
        Expression? current = expr;
        while (current is MemberExpression m) current = m.Expression;
        if (current is null or ConstantExpression) return false;
        if (current is ParameterExpression) return true;

        var finder = new ParameterReferenceFinder();
        finder.Visit(expr);
        return finder.Found;
    }

    private sealed class ParameterReferenceFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found = true;
            return base.VisitParameter(node);
        }
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
            //
            // Extend the outer-scope chain with this translator's own parameter so the
            // sub-translator can resolve references back to it (and to any ancestor beyond
            // it) from inside the nested any()/all() predicate. The root translator's
            // parameter has an empty _parameterPrefix (it needs none for self-reference —
            // "Price gt 10" not "$it/Price gt 10") but must be addressed as "$it" — the
            // OData implicit-iteration-variable name — when referenced from a nested lambda
            // scope, since the range variable there shadows the unqualified form.
            string outerRefName = _parameterPrefix.Length == 0 ? "$it" : _parameterPrefix;
            var outerScopes = new List<OuterScope>(_outerScopes.Count + 1);
            outerScopes.AddRange(_outerScopes);
            outerScopes.Add(new OuterScope(_parameter, outerRefName));

            var subTranslator = new FilterTranslator(lambdaArg.Parameters[0], _namingPolicy, rangeVar, outerScopes);
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
        if (expr is ParameterExpression p)
        {
            if (p == _parameter)
            {
                // Return the parameter prefix (empty string for root, range-var name for sub-translators)
                path = _parameterPrefix;
                return true;
            }

            // Reference to an ancestor any()/all() scope's range variable (or the root
            // parameter, addressed as "$it") from within a nested lambda predicate.
            foreach (OuterScope scope in _outerScopes)
            {
                if (p == scope.Parameter)
                {
                    path = scope.PathPrefix;
                    return true;
                }
            }

            path = "";
            return false;
        }

        if (expr is MemberExpression member && TryGetPropertyPath(member.Expression!, out string? parent))
        {
            // #253 completion: every path segment — navigation hop (e.g. x.Category in x.Category.Name)
            // and structural leaf alike — is resolved through [JsonPropertyName], since the server now
            // renames navigation identifiers too.
            string memberName = ODataMemberName.Resolve(member.Member, _namingPolicy);
            path = parent.Length == 0
                ? memberName
                : $"{parent}/{memberName}";
            return true;
        }

        path = "";
        return false;
    }

    // ── Enum type extraction ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the enum type for the expression if it represents an enum property or literal.
    /// Handles three forms:
    /// <list type="bullet">
    /// <item><c>MemberAccess</c> of enum type</item>
    /// <item><c>Convert(MemberAccess_enum, int)</c> — C# converts enum property TO int for comparison</item>
    /// <item><c>Convert(int, enumType)</c> — C# converts int TO enum type</item>
    /// </list>
    /// </summary>
    private static Type? GetEnumType(Expression expr)
    {
        if (expr.Type.IsEnum) return expr.Type;
        if (expr is UnaryExpression u
            && u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            // Convert TO enum type (e.g. Convert(int constant, ItemStatus))
            if (u.Type.IsEnum) return u.Type;
            // Convert FROM enum type TO int — the operand is the enum member access
            if (u.Operand.Type.IsEnum) return u.Operand.Type;
        }
        return null;
    }

    /// <summary>
    /// Returns the constant value of <paramref name="expr"/> if it is a non-enum constant
    /// (including a <c>Convert</c>-wrapped constant), otherwise <see langword="null"/>.
    /// Used to detect the "integer literal side" of an enum comparison.
    /// </summary>
    private static object? TryGetNonEnumConstantValue(Expression expr)
    {
        // Direct non-enum constant
        if (expr is ConstantExpression ce && !ce.Type.IsEnum)
            return ce.Value;
        // Convert-wrapped non-enum constant (e.g. Convert(1, ItemStatus))
        if (expr is UnaryExpression u
            && u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
            && u.Operand is ConstantExpression innerCe
            && !innerCe.Type.IsEnum)
        {
            return innerCe.Value;
        }
        return null;
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
                        => $"'{value}'",
        _ => string.Format(CultureInfo.InvariantCulture, "{0}", value),
    };

    // The OData ABNF (Part 2 §5.1.1.9, dateTimeOffsetValue) requires an explicit "Z" or numeric
    // offset on every DateTimeOffset literal, and full sub-second precision must round-trip.
    // Delegates to ODataDateTimeLiteralFormatter, shared with ODataKeyFormatter so a $filter
    // literal and an entity-id key literal for the same value format identically.
    private static string FormatDateTime(DateTime dt) => ODataDateTimeLiteralFormatter.FormatDateTime(dt);

    private static string FormatDateTimeOffset(DateTimeOffset dto) => ODataDateTimeLiteralFormatter.FormatDateTimeOffset(dto);
}
