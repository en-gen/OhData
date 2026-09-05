using System;
using System.Linq.Expressions;
using System.Reflection;

namespace OhData.AspNetCore.Mapper;

/// <summary>
/// How one API-model member is obtained from the entity.
/// </summary>
/// <remarks>
/// The vocabulary is deliberately <b>closed</b>. Every member of a mapped model resolves to exactly
/// one of these, which is what makes the mapping enumerable, exhaustively testable, and — for the
/// path kinds — translatable to SQL <i>by construction</i> rather than by hope. An adopter cannot
/// express an untranslatable read the way a hand-written <c>Select(x =&gt; new Dto { … })</c> lets
/// them, which is the failure mode behind a runtime "could not be translated" 500.
/// </remarks>
public enum ModelBindingKind
{
    /// <summary>
    /// <c>Property(d =&gt; d.Code).From(o =&gt; o.Code)</c> — same member on the entity.
    /// Translatable and invertible.
    /// </summary>
    Direct,

    /// <summary>
    /// <c>Property(d =&gt; d.OrderCode).From(o =&gt; o.Code)</c> — a different member on the entity.
    /// Translatable and invertible: the write side is the same rename read backwards.
    /// </summary>
    Rename,

    /// <summary>
    /// <c>Property(d =&gt; d.CategoryName).From(o =&gt; o.Category.Name)</c> — a member reached
    /// through one or more references. Translatable (it becomes a JOIN) but <b>not</b> invertible:
    /// writing it cannot know whether to update the related row or create one.
    /// </summary>
    Path,

    /// <summary>
    /// <c>Reference(d =&gt; d.Category).From(o =&gt; o.Category)</c> — a single-valued navigation whose
    /// target is itself a mapped model. Translatable (the entity-side path is a member path); not
    /// invertible, for the same reason <see cref="Path"/> is not.
    /// </summary>
    Reference,

    /// <summary>
    /// <c>Collection(d =&gt; d.Tags).From(o =&gt; o.Links).Element(l =&gt; l.Tag)</c> — a collection
    /// reshaped on the way out, which is how a many-to-many join entity is elided from the wire.
    /// Translatable; not invertible (writing it is relationship management).
    /// </summary>
    Collection,

    /// <summary>
    /// <c>Property(d =&gt; d.DisplayName).Format($"{o.First} {o.Last}")</c> — an interpolation the
    /// mapper decomposes into folded <c>string.Concat</c>, which SQL evaluates.
    /// <para>
    /// The interpolation is <b>not</b> handed to the provider as written: measured on EF Core 10,
    /// <c>$"{a} {b}"</c> projects (the final <c>Select</c> is evaluated client-side) but throws when
    /// filtered, and the params-array <c>Concat(string[])</c> overload behaves the same way. Only
    /// folded two-argument calls become <c>||</c> in SQL and can therefore appear in a
    /// <c>WHERE</c>.
    /// </para>
    /// </summary>
    Format,

    /// <summary>
    /// An arbitrary expression over the entity. The only kind whose translatability is not
    /// guaranteed by its shape, so it is the only kind the startup probe has to check.
    /// </summary>
    Compute,

    /// <summary>
    /// Declared to have no entity source. Excluded from the projection and from write validation,
    /// and marked non-queryable in the EDM so <c>$filter</c> over it is a clean 400 rather than a
    /// provider failure.
    /// </summary>
    Ignored,
}

/// <summary>
/// One resolved model-member binding: the model member, how it is obtained, and the entity-side
/// expression that obtains it.
/// </summary>
/// <remarks>
/// This is the single fact every consumer reads — the projection, the predicate/sort substituter,
/// the write map and the EDM annotations all derive from it rather than re-deriving the
/// correspondence themselves. That is the "one site, N consumers" rule the framework already
/// enforces elsewhere; two independent derivations of one correspondence is the defect shape this
/// repository has hit repeatedly.
/// </remarks>
public sealed class ModelMemberBinding
{
    internal ModelMemberBinding(
        MemberInfo modelMember,
        ModelBindingKind kind,
        LambdaExpression? source,
        LambdaExpression? elementSource,
        Type? elementEntityType,
        Type? elementModelType = null)
    {
        ModelMember = modelMember;
        Kind = kind;
        Source = source;
        ElementSource = elementSource;
        ElementEntityType = elementEntityType;
        ElementModelType = elementModelType;
    }

    /// <summary>The member on the API model this binding populates.</summary>
    public MemberInfo ModelMember { get; }

    /// <summary>Which of the closed set of binding shapes this is.</summary>
    public ModelBindingKind Kind { get; }

    /// <summary>
    /// The entity-side expression, as a lambda over the entity type. <c>null</c> only for
    /// <see cref="ModelBindingKind.Ignored"/>.
    /// </summary>
    public LambdaExpression? Source { get; }

    /// <summary>
    /// For <see cref="ModelBindingKind.Collection"/>: how one element of the source collection
    /// reaches the element entity — <c>l =&gt; l.Tag</c> for a join entity, or the identity for a
    /// plain one-to-many.
    /// </summary>
    public LambdaExpression? ElementSource { get; }

    /// <summary>
    /// For <see cref="ModelBindingKind.Collection"/>: the entity type of one element. For
    /// <see cref="ModelBindingKind.Reference"/>: the entity type the reference points at.
    /// </summary>
    public Type? ElementEntityType { get; }

    /// <summary>
    /// The model type of one element (<see cref="ModelBindingKind.Collection"/>) or of the target
    /// (<see cref="ModelBindingKind.Reference"/>). Both resolve to a map of their own, which is what
    /// lets a nested lambda and a nested <c>$expand</c> substitute through the target's own bindings
    /// rather than repeating them at every use.
    /// </summary>
    public Type? ElementModelType { get; }

    /// <summary>Whether this binding names a navigation the request may <c>$expand</c>.</summary>
    public bool IsNavigation => Kind is ModelBindingKind.Collection or ModelBindingKind.Reference;

    /// <summary>
    /// Whether a write can be routed back through this binding. Only a member that names exactly one
    /// entity member can be: a path cannot decide whether to update or create the related row, a
    /// reshaped collection is relationship management, and a computed value has no inverse at all.
    /// </summary>
    public bool IsInvertible => Kind is ModelBindingKind.Direct or ModelBindingKind.Rename;

    /// <summary>
    /// Whether <c>$filter</c>/<c>$orderby</c> over this member can be pushed to the provider. False
    /// only for <see cref="ModelBindingKind.Compute"/> bindings the startup probe rejected and for
    /// <see cref="ModelBindingKind.Ignored"/>; those are marked non-queryable in the EDM so the
    /// request is refused by the existing allowlist path instead of failing in the provider.
    /// </summary>
    public bool IsQueryable { get; internal set; } = true;

    /// <summary>The single entity member a write can target, for an invertible binding.</summary>
    public MemberInfo? InvertibleTarget =>
        IsInvertible && Source?.Body is MemberExpression m ? m.Member : null;

    /// <inheritdoc />
    public override string ToString() =>
        $"{ModelMember.DeclaringType?.Name}.{ModelMember.Name} <- {Kind}" +
        (Source is null ? "" : $" ({Source.Body})");
}
