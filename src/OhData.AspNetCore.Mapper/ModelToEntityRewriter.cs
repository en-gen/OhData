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
/// <para>
/// This is why a purpose-built mapper is far smaller than a general one. A general mapper's
/// configuration is an object graph of resolvers and conditions, so reversing it means interpreting
/// that graph. Here the correspondence is already an expression, so "reversing" it is a bindings
/// lookup plus substitution — no interpretation, and nothing to keep in step with a second
/// representation.
/// </para>
/// <para>
/// The hard case, and the one the whole design rests on, is a lambda over a <b>reshaped</b>
/// collection: <c>d =&gt; d.Tags.Any(t =&gt; t.Label == "sale")</c> against a model whose
/// <c>Tags</c> comes from <c>o.Links</c> via <c>l =&gt; l.Tag</c> must become
/// <c>o =&gt; o.Links.Any(l =&gt; l.Tag.Label == "sale")</c>. Measured translating to a correlated
/// <c>EXISTS</c>, with the join entity absent from the model entirely.
/// </para>
/// </remarks>
public sealed class ModelToEntityRewriter : ExpressionVisitor
{
    private static readonly MethodInfo s_concat2 =
        typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string) })!;

    private static readonly Regex s_placeholder = new(@"(\{\d+\})", RegexOptions.Compiled);

    private readonly ModelMap _map;
    private readonly ParameterExpression _entityParameter;
    private readonly Dictionary<ParameterExpression, Expression> _parameterSubstitutions = new();
    private readonly Func<Type, ModelMap?> _resolveMap;

    /// <summary>
    /// Creates a rewriter for one map.
    /// </summary>
    /// <param name="map">The correspondence to substitute through.</param>
    /// <param name="resolveMap">
    /// Resolves the map for a nested model type, so a lambda over a mapped collection can substitute
    /// through the element's own declared bindings rather than repeating them inline.
    /// </param>
    public ModelToEntityRewriter(ModelMap map, Func<Type, ModelMap?>? resolveMap = null)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _entityParameter = Expression.Parameter(map.EntityType, "e");
        _resolveMap = resolveMap ?? (_ => null);
    }

    /// <summary>The entity-side parameter every rewritten expression is written against.</summary>
    public ParameterExpression EntityParameter => _entityParameter;

    /// <summary>Rewrites a model-shaped predicate into an entity-shaped one.</summary>
    public LambdaExpression RewriteLambda(LambdaExpression modelLambda)
    {
        if (modelLambda is null) throw new ArgumentNullException(nameof(modelLambda));

        _parameterSubstitutions[modelLambda.Parameters[0]] = _entityParameter;
        Expression body = Visit(modelLambda.Body);
        return Expression.Lambda(
            typeof(Func<,>).MakeGenericType(_map.EntityType, modelLambda.ReturnType),
            body,
            _entityParameter);
    }

    /// <summary>Rewrites a bare model-shaped expression (a sort key, say) into entity terms.</summary>
    public Expression RewriteBody(Expression modelBody, ParameterExpression modelParameter)
    {
        _parameterSubstitutions[modelParameter] = _entityParameter;
        return Visit(modelBody);
    }

    /// <summary>
    /// The entity-side expression for one model member, ready to bind into a projection or compare
    /// in a predicate. Returns <c>null</c> for a member with no entity source.
    /// </summary>
    public Expression? BindingFor(ModelMemberBinding binding, Expression entityInstance)
    {
        if (binding.Kind == ModelBindingKind.Ignored || binding.Source is null) return null;

        return binding.Kind == ModelBindingKind.Format
            ? DecomposeFormat(binding.Source, entityInstance)
            : Inline(binding.Source, entityInstance);
    }

    /// <inheritdoc />
    protected override Expression VisitParameter(ParameterExpression node) =>
        _parameterSubstitutions.TryGetValue(node, out Expression? sub) ? sub : node;

    /// <inheritdoc />
    protected override Expression VisitMember(MemberExpression node)
    {
        if (node.Expression is ParameterExpression p
            && _parameterSubstitutions.TryGetValue(p, out Expression? entityInstance)
            && _map.Find(node.Member.Name) is { } binding)
        {
            Expression? bound = BindingFor(binding, entityInstance);
            if (bound is not null) return bound;

            throw new InvalidOperationException(
                $"'{_map.ModelType.Name}.{node.Member.Name}' has no entity source (it is declared " +
                $"Ignore()), so it cannot appear in a query. It is marked non-queryable in the model, " +
                $"so this should have been refused before reaching the provider.");
        }

        return base.VisitMember(node);
    }

    /// <inheritdoc />
    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // d.Tags.Any(t => …)  ->  e.Links.Any(l => …[t := l.Tag])
        if (node.Arguments.Count == 2
            && node.Method.Name is "Any" or "All"
            && IsMappedCollection(node.Arguments[0], out Expression? owner, out ModelMemberBinding? binding))
        {
            return RewriteCollectionLambda(node, owner!, binding!);
        }

        // `Tags/any()` binds to the predicate-less Any(source) overload, whose element type is the
        // MODEL's. Substituting the source alone would leave Any<TagDto> applied to a
        // List<ProductTag>, so the call is rebuilt over the entity element type as well. Measured:
        // without this the request is a 500, not a wrong answer.
        if (node.Arguments.Count == 1
            && node.Method.Name == "Any"
            && IsMappedCollection(node.Arguments[0], out Expression? bareOwner, out ModelMemberBinding? bareBinding))
        {
            Expression source = Inline(bareBinding!.Source!, bareOwner!);
            Type sourceElement = ElementTypeOf(source.Type);

            MethodInfo any = typeof(Enumerable).GetMethods()
                .First(m => m.Name == "Any" && m.GetParameters().Length == 1)
                .MakeGenericMethod(sourceElement);

            return Expression.Call(any, source);
        }

        return base.VisitMethodCall(node);
    }

    private bool IsMappedCollection(
        Expression candidate, out Expression? owner, out ModelMemberBinding? binding)
    {
        owner = null;
        binding = null;

        if (candidate is not MemberExpression member
            || member.Expression is not ParameterExpression parameter
            || !_parameterSubstitutions.TryGetValue(parameter, out Expression? instance)
            || _map.Find(member.Member.Name) is not { Kind: ModelBindingKind.Collection } found)
        {
            return false;
        }

        owner = instance;
        binding = found;
        return true;
    }

    private Expression RewriteCollectionLambda(
        MethodCallExpression node, Expression owner, ModelMemberBinding binding)
    {
        var modelLambda = (LambdaExpression)Unquote(node.Arguments[1]);
        Expression source = Inline(binding.Source!, owner);

        Type sourceElement = ElementTypeOf(source.Type);
        ParameterExpression sourceParameter = Expression.Parameter(sourceElement, "x");

        // The range variable is a MODEL element; reach the entity element through the declared
        // element path, then substitute that element's own map through the inner body.
        Expression entityElement = binding.ElementSource is null
            ? sourceParameter
            : Inline(binding.ElementSource, sourceParameter);

        Type modelElement = modelLambda.Parameters[0].Type;
        ModelMap? elementMap = _resolveMap(modelElement);

        Expression innerBody;
        if (elementMap is not null)
        {
            var inner = new ModelToEntityRewriter(elementMap, _resolveMap);
            inner._parameterSubstitutions[modelLambda.Parameters[0]] = entityElement;
            innerBody = inner.Visit(modelLambda.Body);
        }
        else
        {
            // No declared element map: the model element and the entity element are the same shape,
            // so the range variable substitutes directly. Refusing here instead would break the
            // ordinary one-to-many case, which needs no element map at all.
            _parameterSubstitutions[modelLambda.Parameters[0]] = entityElement;
            innerBody = Visit(modelLambda.Body);
            _parameterSubstitutions.Remove(modelLambda.Parameters[0]);
        }

        MethodInfo op = typeof(Enumerable).GetMethods()
            .First(m => m.Name == node.Method.Name
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2)
            .MakeGenericMethod(sourceElement);

        return Expression.Call(op, source, Expression.Lambda(innerBody, sourceParameter));
    }

    /// <summary>
    /// Rewrites <c>FormattableStringFactory.Create("{0} {1}", a, b)</c> into folded two-argument
    /// <c>string.Concat</c>.
    /// </summary>
    /// <remarks>
    /// Folded two-argument calls are what become <c>||</c> in SQL. Measured on EF Core 10: the
    /// interpolation itself and the params-array <c>Concat(string[])</c> overload both project — the
    /// final <c>Select</c> is evaluated client-side — but both throw when filtered, so neither can
    /// carry <c>$filter</c>. Emitting the array overload here would produce a member that looks
    /// mapped and cannot be queried.
    /// </remarks>
    private Expression DecomposeFormat(LambdaExpression interpolation, Expression entityInstance)
    {
        Expression body = Inline(interpolation, entityInstance);

        if (body is not MethodCallExpression call
            || call.Method.DeclaringType != typeof(FormattableStringFactory)
            || call.Method.Name != nameof(FormattableStringFactory.Create))
        {
            throw new InvalidOperationException(
                "Format(...) expects a string interpolation, for example " +
                "Format(o => $\"{o.First} {o.Last}\").");
        }

        string format = (string)((ConstantExpression)call.Arguments[0]).Value!;
        List<Expression> args = ((NewArrayExpression)call.Arguments[1]).Expressions
            .Select(Unconvert)
            .ToList();

        var parts = new List<Expression>();
        foreach (string token in s_placeholder.Split(format))
        {
            if (token.Length == 0) continue;

            Match m = s_placeholder.Match(token);
            if (m.Success && token == m.Value)
            {
                Expression arg = args[int.Parse(token.Trim('{', '}'))];
                parts.Add(arg.Type == typeof(string)
                    ? arg
                    : Expression.Call(arg, typeof(object).GetMethod(nameof(ToString))!));
            }
            else
            {
                parts.Add(Expression.Constant(token));
            }
        }

        return parts.Count == 0
            ? Expression.Constant(string.Empty)
            : parts.Aggregate((a, b) => Expression.Call(s_concat2, a, b));
    }

    /// <summary>Replaces a lambda's parameter with a concrete instance expression.</summary>
    private static Expression Inline(LambdaExpression lambda, Expression instance) =>
        new ParameterInliner(lambda.Parameters[0], instance).Visit(lambda.Body);

    private static Expression Unquote(Expression e) =>
        e is UnaryExpression { NodeType: ExpressionType.Quote } u ? u.Operand : e;

    private static Expression Unconvert(Expression e) =>
        e is UnaryExpression { NodeType: ExpressionType.Convert } u ? u.Operand : e;

    private static Type ElementTypeOf(Type collectionType) =>
        collectionType.IsArray
            ? collectionType.GetElementType()!
            : collectionType.GetInterfaces()
                  .Concat(new[] { collectionType })
                  .First(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                  .GetGenericArguments()[0];

    private sealed class ParameterInliner : ExpressionVisitor
    {
        private readonly ParameterExpression _parameter;
        private readonly Expression _replacement;

        public ParameterInliner(ParameterExpression parameter, Expression replacement)
        {
            _parameter = parameter;
            _replacement = replacement;
        }

        protected override Expression VisitParameter(ParameterExpression node) =>
            node == _parameter ? _replacement : node;
    }
}
