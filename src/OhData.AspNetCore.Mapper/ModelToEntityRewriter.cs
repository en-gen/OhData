using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace OhData.AspNetCore.Mapper;

/// <summary>
/// Rewrites an expression written against the API model into one written against the entity, by
/// substituting the declared bindings of a <see cref="ModelMap"/>.
/// </summary>
/// <remarks>
/// This is why a purpose-built mapper is far smaller than a general one: the correspondence is
/// already an expression, so "reversing" it is a bindings lookup plus substitution — no
/// interpretation, and nothing to keep in step with a second representation.
/// </remarks>
internal sealed class ModelToEntityRewriter : ExpressionVisitor
{
    private static readonly MethodInfo s_concat2 =
        typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string) })!;

    // Captures a placeholder's optional alignment and format specifier as well as its index, so both
    // can be detected rather than silently swallowed by a narrower pattern.
    private static readonly Regex s_placeholder =
        new(@"\{(\d+)(,-?\d+)?(:[^}]*)?\}", RegexOptions.Compiled);

    private readonly ModelMap _map;
    private readonly ModelMapRegistry _registry;
    private readonly ParameterExpression _entityParameter;

    // A model parameter resolves to an entity-side expression AND the map its members belong to. The
    // map is half of it: a lambda over a mapped collection introduces a range variable of the ELEMENT
    // model, and resolving its members against the root map would bind the wrong member entirely.
    private readonly Dictionary<ParameterExpression, (Expression Instance, ModelMap Map)> _scopes = new();

    public ModelToEntityRewriter(ModelMap map, ModelMapRegistry registry)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _entityParameter = Expression.Parameter(map.EntityType, "e");
    }

    /// <summary>The entity-side parameter every rewritten expression is written against.</summary>
    public ParameterExpression EntityParameter => _entityParameter;

    /// <summary>Rewrites a model-shaped lambda into an entity-shaped one.</summary>
    public LambdaExpression RewriteLambda(LambdaExpression modelLambda)
    {
        if (modelLambda is null) throw new ArgumentNullException(nameof(modelLambda));

        _scopes[modelLambda.Parameters[0]] = (_entityParameter, _map);
        Expression body = Visit(modelLambda.Body);
        return Expression.Lambda(
            typeof(Func<,>).MakeGenericType(_map.EntityType, body.Type),
            body,
            _entityParameter);
    }

    /// <summary>
    /// The entity-side expression for one model member, in the model member's own type, guarded
    /// against an unset reference on the way.
    /// </summary>
    /// <remarks>
    /// The single place a binding becomes an expression. The projection and the predicate both come
    /// through here, so a member cannot mean one thing when it is served and another when it is
    /// filtered.
    /// </remarks>
    public Expression BindingFor(ModelMemberBinding binding, Expression entityInstance)
    {
        if (binding is null) throw new ArgumentNullException(nameof(binding));

        if (binding.Kind == ModelBindingKind.Ignored || binding.Source is null)
        {
            // Unreachable from a query: UseMap removes an Ignore()d member from the EDM, so the
            // parser refuses it before any binder sees it. An invariant assertion, not an error path.
            throw new InvalidOperationException(
                $"'{_map.ModelType.Name}.{binding.ModelMember.Name}' has no entity source and cannot " +
                $"be bound. It is declared Ignore(), so it should not be in the EDM at all.");
        }

        Expression value = binding.Kind == ModelBindingKind.Format
            ? DecomposeFormat(binding, entityInstance)
            : MapExpressions.Inline(binding.Source, entityInstance);

        return MapExpressions.GuardAndNarrow(value, MapExpressions.MemberType(binding.ModelMember));
    }

    /// <inheritdoc />
    protected override Expression VisitParameter(ParameterExpression node) =>
        _scopes.TryGetValue(node, out (Expression Instance, ModelMap Map) scope) ? scope.Instance : node;

    /// <inheritdoc />
    protected override Expression VisitMember(MemberExpression node) =>
        TryResolveModelPath(node, out Expression? bound) ? bound! : base.VisitMember(node);

    /// <inheritdoc />
    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // Matched on the declaring type as well as the name: a user method called Any over a mapped
        // collection would otherwise be rebuilt as Enumerable.Any and quietly mean something else.
        if (node.Method.DeclaringType == typeof(Enumerable) || node.Method.DeclaringType == typeof(Queryable))
        {
            if (node.Arguments.Count == 2
                && node.Method.Name is "Any" or "All" or "Count" or "LongCount"
                && TryResolveCollection(node.Arguments[0], out Expression? source, out ModelMemberBinding? binding))
            {
                return RewriteCollectionOperator(node, source!, binding!);
            }

            // `Tags/any()` and `Tags/$count` bind to the argument-less overloads, whose element type
            // is the MODEL's. Substituting only the source would leave Any<TagDto> applied to a
            // List<ProductTag>; the call has to be rebuilt over the entity element type too.
            if (node.Arguments.Count == 1
                && node.Method.Name is "Any" or "Count" or "LongCount"
                && TryResolveCollection(node.Arguments[0], out Expression? bare, out _))
            {
                MethodInfo op = typeof(Enumerable).GetMethods()
                    .First(m => m.Name == node.Method.Name && m.GetParameters().Length == 1)
                    .MakeGenericMethod(MapExpressions.ElementTypeOf(bare!.Type));

                return Expression.Call(op, bare);
            }
        }

        return base.VisitMethodCall(node);
    }

    /// <summary>
    /// Resolves a model member path — <c>d.Title</c>, <c>d.Category.Name</c>, <c>t.Label</c> — into
    /// entity terms, hopping through each <see cref="ModelBindingKind.Reference"/> on the way.
    /// </summary>
    /// <remarks>
    /// A path of more than one member is why this is a walk rather than a single lookup. Handling
    /// only <c>parameter.Member</c> left <c>$filter=Category/Name eq 'x'</c> to the base visitor,
    /// which rebuilt <c>CategoryDto.Name</c> over an entity-typed instance and threw
    /// <i>"Property 'Name' is not defined for type 'Category'"</i> — a 500 on a construct the guide
    /// advertises.
    /// </remarks>
    private bool TryResolveModelPath(MemberExpression node, out Expression? bound)
    {
        bound = null;

        var members = new List<MemberInfo>();
        Expression? cursor = node;
        while (cursor is MemberExpression m)
        {
            members.Add(m.Member);
            cursor = m.Expression;
        }

        if (cursor is not ParameterExpression parameter
            || !_scopes.TryGetValue(parameter, out (Expression Instance, ModelMap Map) scope))
        {
            return false;
        }

        members.Reverse();
        Expression accumulated = scope.Instance;
        ModelMap map = scope.Map;

        for (int i = 0; i < members.Count; i++)
        {
            ModelMemberBinding? binding = map.Find(members[i].Name);

            // A member the map never declares is not this rewriter's to invent. Left alone, it fails
            // loudly at the provider rather than binding to something plausible.
            if (binding is null) return false;

            if (i == members.Count - 1)
            {
                accumulated = BindingFor(binding, accumulated);
                break;
            }

            if (binding.Kind != ModelBindingKind.Reference) return false;

            accumulated = MapExpressions.Inline(binding.Source!, accumulated);
            map = RequireMap(binding);
        }

        bound = accumulated;
        return true;
    }

    /// <summary>Resolves a mapped collection member to its entity-side source.</summary>
    private bool TryResolveCollection(
        Expression candidate, out Expression? source, out ModelMemberBinding? binding)
    {
        source = null;
        binding = null;

        if (candidate is not MemberExpression member
            || member.Expression is not ParameterExpression parameter
            || !_scopes.TryGetValue(parameter, out (Expression Instance, ModelMap Map) scope)
            || scope.Map.Find(member.Member.Name) is not
            { Kind: ModelBindingKind.Collection } found)
        {
            return false;
        }

        source = MapExpressions.Inline(found.Source!, scope.Instance);
        binding = found;
        return true;
    }

    private Expression RewriteCollectionOperator(
        MethodCallExpression node, Expression source, ModelMemberBinding binding)
    {
        var modelLambda = (LambdaExpression)Unquote(node.Arguments[1]);

        Type sourceElement = MapExpressions.ElementTypeOf(source.Type);
        ParameterExpression sourceParameter = Expression.Parameter(sourceElement, "x");

        // The range variable is a MODEL element; reach the entity element through the declared
        // element path, then resolve the inner body against the ELEMENT's map.
        Expression entityElement = binding.ElementSource is null
            ? sourceParameter
            : MapExpressions.Inline(binding.ElementSource, sourceParameter);

        ParameterExpression rangeVariable = modelLambda.Parameters[0];

        // The OUTER scopes stay in place, so a lambda referring to the parent's members --
        // `Tags/any(t: t/Label eq Title)` -- still substitutes them. Seeding a fresh rewriter with
        // only the inner parameter left `$it.Title` in the entity expression, which EF refused to
        // translate; and it did so only on the branch that had an element map, so one OData
        // construct had two answers.
        _scopes[rangeVariable] = (entityElement, RequireMap(binding));
        Expression innerBody;
        try
        {
            innerBody = Visit(modelLambda.Body);
        }
        finally
        {
            _scopes.Remove(rangeVariable);
        }

        MethodInfo op = typeof(Enumerable).GetMethods()
            .First(m => m.Name == node.Method.Name
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2)
            .MakeGenericMethod(sourceElement);

        return Expression.Call(op, source, Expression.Lambda(innerBody, sourceParameter));
    }

    private ModelMap RequireMap(ModelMemberBinding binding) =>
        _registry.Find(binding.ElementModelType!)
        ?? throw new InvalidOperationException(
            $"'{binding.ModelMember.DeclaringType?.Name}.{binding.ModelMember.Name}' reaches model " +
            $"type '{binding.ElementModelType?.Name}', which has no map. Startup validation should " +
            $"have refused this.");

    /// <summary>
    /// Rewrites <c>FormattableStringFactory.Create("{0} {1}", a, b)</c> into folded two-argument
    /// <c>string.Concat</c>.
    /// </summary>
    /// <remarks>
    /// Folded two-argument calls are what become <c>||</c> in SQL. Measured on EF Core 10: the
    /// interpolation itself and the params-array <c>Concat(string[])</c> overload both project — the
    /// final <c>Select</c> is evaluated client-side — but both throw when filtered, so neither can
    /// carry <c>$filter</c>.
    /// </remarks>
    private Expression DecomposeFormat(ModelMemberBinding binding, Expression entityInstance)
    {
        Expression body = MapExpressions.Inline(binding.Source!, entityInstance);
        string where = $"{_map.ModelType.Name}.{binding.ModelMember.Name}";

        if (body is not MethodCallExpression call
            || call.Method.DeclaringType != typeof(FormattableStringFactory)
            || call.Method.Name != nameof(FormattableStringFactory.Create))
        {
            throw new InvalidOperationException(
                $"'{where}': Format(...) expects a string interpolation, for example " +
                $"Format(o => $\"{{o.First}} {{o.Last}}\").");
        }

        string format = (string)((ConstantExpression)call.Arguments[0]).Value!;
        List<Expression> args = ((NewArrayExpression)call.Arguments[1]).Expressions
            .Select(Unconvert)
            .ToList();

        var parts = new List<Expression>();
        int cursor = 0;

        foreach (Match match in s_placeholder.Matches(format))
        {
            if (match.Index > cursor) parts.Add(Literal(format.Substring(cursor, match.Index - cursor)));
            cursor = match.Index + match.Length;

            // An alignment or a format specifier has no SQL equivalent, and the previous pattern
            // matched neither -- so `$"{o.Price:C}"` emitted the literal text "{0:C}" and dropped the
            // value, on every row, under a 200.
            if (match.Groups[2].Success || match.Groups[3].Success)
            {
                throw new InvalidOperationException(
                    $"'{where}': the interpolation uses '{match.Value}'. An alignment or format " +
                    $"specifier cannot be translated to SQL. Format the value in the database with " +
                    $"Compute(...), or expose the raw member and format it on the client.");
            }

            Expression arg = args[int.Parse(match.Groups[1].Value)];
            parts.Add(arg.Type == typeof(string)
                ? arg
                : Expression.Call(arg, typeof(object).GetMethod(nameof(ToString))!));
        }

        if (cursor < format.Length) parts.Add(Literal(format.Substring(cursor)));

        return parts.Count == 0
            ? Expression.Constant(string.Empty)
            : parts.Aggregate((a, b) => Expression.Call(s_concat2, a, b));
    }

    // A composite format string escapes a brace by doubling it, so the literal segments have to be
    // unescaped or `$"{{literal}}"` reaches the wire with its braces still doubled.
    private static Expression Literal(string text) =>
        Expression.Constant(text.Replace("{{", "{", StringComparison.Ordinal)
                                .Replace("}}", "}", StringComparison.Ordinal));

    private static Expression Unquote(Expression e) =>
        e is UnaryExpression { NodeType: ExpressionType.Quote } u ? u.Operand : e;

    private static Expression Unconvert(Expression e) =>
        e is UnaryExpression { NodeType: ExpressionType.Convert } u ? u.Operand : e;
}
