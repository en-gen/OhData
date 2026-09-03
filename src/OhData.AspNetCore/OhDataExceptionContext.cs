using Microsoft.AspNetCore.OData.Deltas;

namespace OhData;

/// <summary>
/// What the framework knows about the request at the point user code threw, handed to a
/// <c>ConfigureExceptions</c> mapping so it can craft a useful message (#581).
/// <para>
/// A union whose members are populated per seam, discriminated by <see cref="Operation"/> — switch
/// on that and read what it implies rather than null-checking defensively. Inheritance cannot serve
/// here: one mapping lambda serves every seam, so its parameter must be a single type, and a
/// hierarchy would force a downcast — the same "maybe" with extra ceremony.
/// </para>
/// <para>
/// A <c>readonly struct</c> built inside the seam's exception filter, so a request that does not
/// throw allocates nothing.
/// </para>
/// </summary>
public readonly struct OhDataExceptionContext<TModel>
    where TModel : class
{
    internal OhDataExceptionContext(
        string entitySetName,
        OhDataOperation operation,
        string? queryString,
        object? key,
        TModel? model,
        Delta<TModel>? delta,
        string? navigation)
    {
        EntitySetName = entitySetName;
        Operation = operation;
        QueryString = queryString;
        Key = key;
        Model = model;
        Delta = delta;
        Navigation = navigation;
    }

    /// <summary>The entity set whose profile declared the handler. Always populated.</summary>
    public string EntitySetName { get; }

    /// <summary>
    /// Which category the seam belongs to. Always exactly one flag, never a combination, and it is
    /// what says which of the members below are populated.
    /// </summary>
    public OhDataOperation Operation { get; }

    /// <summary>
    /// The request's raw query string, or <c>null</c> when there was none. The raw form rather than
    /// a parsed <c>ODataQueryOptions</c>: a message wants something to interpolate, and the parsed
    /// form would widen every profile's dependency surface for the sake of one error path.
    /// </summary>
    public string? QueryString { get; }

    /// <summary>The parsed key on a keyed route; <c>null</c> on collection routes.</summary>
    public object? Key { get; }

    /// <summary>The deserialized body on <c>Post</c> and <c>Put</c>; <c>null</c> elsewhere.</summary>
    public TModel? Model { get; }

    /// <summary>The change set on <c>Patch</c>; <c>null</c> elsewhere.</summary>
    public Delta<TModel>? Delta { get; }

    /// <summary>The navigation property name when the throw came from a navigation seam.</summary>
    public string? Navigation { get; }
}
