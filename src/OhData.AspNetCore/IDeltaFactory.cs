using Microsoft.AspNetCore.OData.Deltas;

namespace OhData;

/// <summary>
/// A dependency-free, convention-first delta mapper. Injected once (DI singleton) and called for
/// whatever <c>(model, entity)</c> pair a handler needs — mirroring AutoMapper's single
/// <c>IMapper.Map&lt;,&gt;()</c> rather than a closed generic per pair. Mappings are declared in a
/// <see cref="DeltaProfile"/> and compiled/validated once at startup.
/// <para>
/// Both type arguments are given explicitly at the call site: <c>TModel</c> is
/// inferable from the argument but <c>TEntity</c> (return-only) is not. The result
/// is always a <c>Delta&lt;TEntity&gt;</c> — change-set and updatable-property allowlist preserved
/// — which the handler applies with the built-in <c>Delta&lt;TEntity&gt;.Patch(entity)</c> and then
/// persists. The framework never applies or persists.
/// </para>
/// </summary>
public interface IDeltaFactory
{
    /// <summary>
    /// Translates a <c>Delta&lt;TModel&gt;</c> (e.g. from a PATCH handler) into a
    /// <c>Delta&lt;TEntity&gt;</c>, carrying only the model's changed properties across the
    /// DTO→entity boundary (rename/convert applied, ignored properties dropped).
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// No delta mapping was registered for the <c>(TModel, TEntity)</c> pair.
    /// </exception>
    Delta<TEntity> Create<TModel, TEntity>(Delta<TModel> delta)
        where TModel : class
        where TEntity : class;

    /// <summary>
    /// Translates a full <typeparamref name="TModel"/> instance (e.g. from a PUT/POST handler) into
    /// a <c>Delta&lt;TEntity&gt;</c> whose changed set is every mapped, non-ignored property.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// No delta mapping was registered for the <c>(TModel, TEntity)</c> pair.
    /// </exception>
    Delta<TEntity> Create<TModel, TEntity>(TModel model)
        where TModel : class
        where TEntity : class;
}
