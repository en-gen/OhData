using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace OhData.AspNetCore.Mapper;

/// <summary>
/// The declared correspondence between an API model and the entity behind it.
/// </summary>
/// <remarks>
/// Built by <see cref="ModelMapBuilder{TEntity, TModel}"/> and read by everything else. It is data,
/// not behaviour: the projection composer, the substituter, the write map and the EDM annotations
/// are all derived from the same <see cref="Bindings"/>, so no two of them can disagree about where
/// a member comes from.
/// </remarks>
public sealed class ModelMap
{
    private readonly Dictionary<string, ModelMemberBinding> _byModelMember;

    internal ModelMap(Type entityType, Type modelType, IReadOnlyList<ModelMemberBinding> bindings)
    {
        EntityType = entityType;
        ModelType = modelType;
        Bindings = new ReadOnlyCollection<ModelMemberBinding>(bindings.ToList());
        _byModelMember = bindings.ToDictionary(b => b.ModelMember.Name, StringComparer.Ordinal);
    }

    /// <summary>The persistence type the query runs against.</summary>
    public Type EntityType { get; }

    /// <summary>The type on the wire and in <c>$metadata</c>.</summary>
    public Type ModelType { get; }

    /// <summary>Every declared binding, in declaration order.</summary>
    public IReadOnlyList<ModelMemberBinding> Bindings { get; }

    /// <summary>
    /// Looks up a binding by model member name.
    /// </summary>
    /// <remarks>
    /// <b>Ordinal</b>: the names come from the EDM, which is built from the CLR type, so they are
    /// the CLR spellings on both sides — never client-supplied text.
    /// </remarks>
    public ModelMemberBinding? Find(string modelMemberName) =>
        _byModelMember.TryGetValue(modelMemberName, out ModelMemberBinding? b) ? b : null;

    /// <summary>Bindings a request may <c>$expand</c>.</summary>
    public IEnumerable<ModelMemberBinding> Navigations => Bindings.Where(b => b.IsNavigation);
}

/// <summary>
/// Declares where each member of <typeparamref name="TModel"/> comes from on
/// <typeparamref name="TEntity"/>.
/// </summary>
/// <remarks>
/// The adopter states correspondences; the mapper composes whatever query shape a given request
/// needs.
/// </remarks>
public sealed class ModelMapBuilder<TEntity, TModel>
    where TEntity : class
    where TModel : class
{
    private readonly List<ModelMemberBinding> _bindings = new();

    /// <summary>Begins a scalar member declaration.</summary>
    public ScalarBinding Property<TValue>(Expression<Func<TModel, TValue>> member) =>
        new(this, MemberOf(member));

    /// <summary>
    /// Declares a single-valued navigation whose target is itself a mapped model.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>Property(...).From(o =&gt; o.Category.Name)</c>, which reaches a scalar
    /// <i>through</i> a reference and is a <see cref="ModelBindingKind.Path"/>. This one names the
    /// whole target, so <c>$expand</c> can ask for it and a nested <c>$filter</c> can substitute
    /// through the target's own map.
    /// </remarks>
    public ModelMapBuilder<TEntity, TModel> Reference<TTargetModel, TTargetEntity>(
        Expression<Func<TModel, TTargetModel>> member,
        Expression<Func<TEntity, TTargetEntity>> source)
    {
        Add(new ModelMemberBinding(
            MemberOf(member), ModelBindingKind.Reference, source, null,
            typeof(TTargetEntity), typeof(TTargetModel)));
        return this;
    }

    /// <summary>Begins a collection member declaration.</summary>
    public CollectionBinding<TElement> Collection<TElement>(
        Expression<Func<TModel, IEnumerable<TElement>>> member) =>
        new(this, MemberOf(member));

    /// <summary>
    /// Declares that a model member has no entity source. The profile forwards it to
    /// <c>EntitySetProfile.Ignore</c>, so the member leaves the EDM and the wire together and a
    /// <c>$filter</c> over it is the framework's own <c>400</c>.
    /// </summary>
    public ModelMapBuilder<TEntity, TModel> Ignore<TValue>(Expression<Func<TModel, TValue>> member)
    {
        Add(new ModelMemberBinding(MemberOf(member), ModelBindingKind.Ignored, null, null, null));
        return this;
    }

    internal void Add(ModelMemberBinding binding)
    {
        // Refused at the call rather than last-write-wins: keeping one of two declarations silently
        // would make the wire shape depend on declaration order.
        if (_bindings.Any(b => b.ModelMember.Name == binding.ModelMember.Name))
        {
            throw new InvalidOperationException(
                $"'{typeof(TModel).Name}.{binding.ModelMember.Name}' is declared more than once in the " +
                $"map from '{typeof(TEntity).Name}'. Remove the duplicate declaration.");
        }

        _bindings.Add(binding);
    }

    internal ModelMap Build() => new(typeof(TEntity), typeof(TModel), _bindings);

    private static MemberInfo MemberOf<TValue>(Expression<Func<TModel, TValue>> member)
    {
        Expression body = member.Body is UnaryExpression { NodeType: ExpressionType.Convert } u
            ? u.Operand
            : member.Body;

        return body is MemberExpression { Expression: ParameterExpression } m
            ? m.Member
            : throw new ArgumentException(
                $"Expected a single member of '{typeof(TModel).Name}', for example " +
                $"'d => d.{typeof(TModel).GetProperties().FirstOrDefault()?.Name ?? "Member"}', " +
                $"but got '{member.Body}'.",
                nameof(member));
    }

    /// <summary>Declares where one scalar model member comes from.</summary>
    public sealed class ScalarBinding
    {
        private readonly ModelMapBuilder<TEntity, TModel> _owner;
        private readonly MemberInfo _modelMember;

        internal ScalarBinding(ModelMapBuilder<TEntity, TModel> owner, MemberInfo modelMember)
        {
            _owner = owner;
            _modelMember = modelMember;
        }

        /// <summary>
        /// The entity member this comes from — <c>o =&gt; o.Code</c> or <c>o =&gt; o.Category.Name</c>.
        /// A member path is translatable by construction, which is why this, rather than an arbitrary
        /// expression, is the ordinary way to declare a member.
        /// </summary>
        public ModelMapBuilder<TEntity, TModel> From<TValue>(Expression<Func<TEntity, TValue>> source)
        {
            ModelBindingKind kind = ClassifyPath(source.Body, _modelMember.Name);
            _owner.Add(new ModelMemberBinding(_modelMember, kind, source, null, null));
            return _owner;
        }

        /// <summary>
        /// An interpolation, decomposed by the mapper into folded <c>string.Concat</c> so that it
        /// translates to SQL and can therefore be filtered and sorted on. Writing the same
        /// interpolation inside a <c>Compute</c> would project but not filter.
        /// </summary>
        public ModelMapBuilder<TEntity, TModel> Format(
            Expression<Func<TEntity, FormattableString>> interpolation)
        {
            _owner.Add(new ModelMemberBinding(
                _modelMember, ModelBindingKind.Format, interpolation, null, null));
            return _owner;
        }

        /// <summary>
        /// An arbitrary expression over the entity. The only binding kind whose translatability is
        /// not guaranteed by its shape, so it is the one the startup probe checks; a binding the
        /// provider cannot translate is marked non-queryable rather than failing on a request.
        /// </summary>
        public ModelMapBuilder<TEntity, TModel> Compute<TValue>(Expression<Func<TEntity, TValue>> expression)
        {
            _owner.Add(new ModelMemberBinding(
                _modelMember, ModelBindingKind.Compute, expression, null, null));
            return _owner;
        }

        private static ModelBindingKind ClassifyPath(Expression body, string modelMemberName)
        {
            if (body is UnaryExpression { NodeType: ExpressionType.Convert } u) body = u.Operand;

            if (body is not MemberExpression m)
            {
                throw new ArgumentException(
                    $"'{modelMemberName}' was declared with From(...) but '{body}' is not a member " +
                    $"path. Use Format(...) for an interpolation or Compute(...) for anything else.");
            }

            // o => o.X is direct-or-rename; o => o.A.B is a path, which is translatable but has no
            // inverse for writes.
            if (m.Expression is ParameterExpression)
            {
                return m.Member.Name == modelMemberName ? ModelBindingKind.Direct : ModelBindingKind.Rename;
            }

            return ModelBindingKind.Path;
        }
    }

    /// <summary>Declares where one collection model member comes from.</summary>
    public sealed class CollectionBinding<TElement>
    {
        private readonly ModelMapBuilder<TEntity, TModel> _owner;
        private readonly MemberInfo _modelMember;

        internal CollectionBinding(ModelMapBuilder<TEntity, TModel> owner, MemberInfo modelMember)
        {
            _owner = owner;
            _modelMember = modelMember;
        }

        /// <summary>The entity collection this comes from.</summary>
        public SourcedCollection<TElement, TSource> From<TSource>(
            Expression<Func<TEntity, IEnumerable<TSource>>> source) =>
            new(_owner, _modelMember, source ?? throw new ArgumentNullException(nameof(source)));
    }

    /// <summary>
    /// A collection whose entity-side source is declared, awaiting how one source element reaches
    /// the element entity.
    /// </summary>
    /// <remarks>
    /// A separate type rather than more state on <see cref="CollectionBinding{TElement}"/> so
    /// <c>TSource</c> survives to <c>Element</c> — which makes <c>.Element(l =&gt; l.Tag)</c> infer
    /// instead of needing <c>.Element((ProductTag l) =&gt; l.Tag)</c> — and so "Element before From"
    /// is a state that cannot be expressed rather than one checked at runtime.
    /// </remarks>
    /// <typeparam name="TElement">The model type of one element.</typeparam>
    /// <typeparam name="TSource">The entity type of one element of the source collection.</typeparam>
    public sealed class SourcedCollection<TElement, TSource>
    {
        private readonly ModelMapBuilder<TEntity, TModel> _owner;
        private readonly MemberInfo _modelMember;
        private readonly LambdaExpression _source;

        internal SourcedCollection(
            ModelMapBuilder<TEntity, TModel> owner, MemberInfo modelMember, LambdaExpression source)
        {
            _owner = owner;
            _modelMember = modelMember;
            _source = source;
        }

        /// <summary>
        /// How one element of the source collection reaches the element entity —
        /// <c>l =&gt; l.Tag</c> for a join entity, which is how a many-to-many is elided from the
        /// wire entirely.
        /// </summary>
        public ModelMapBuilder<TEntity, TModel> Element<TElementEntity>(
            Expression<Func<TSource, TElementEntity>> element)
        {
            if (element is null) throw new ArgumentNullException(nameof(element));

            _owner.Add(new ModelMemberBinding(
                _modelMember, ModelBindingKind.Collection, _source, element,
                typeof(TElementEntity), typeof(TElement)));
            return _owner;
        }

        /// <summary>Completes a collection whose source elements already are the element entity.</summary>
        public ModelMapBuilder<TEntity, TModel> AsIs()
        {
            ParameterExpression p = Expression.Parameter(typeof(TSource), "e");
            _owner.Add(new ModelMemberBinding(
                _modelMember,
                ModelBindingKind.Collection,
                _source,
                Expression.Lambda(p, p),
                typeof(TSource),
                typeof(TElement)));
            return _owner;
        }
    }
}
