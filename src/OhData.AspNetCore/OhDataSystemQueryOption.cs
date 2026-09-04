using System;

namespace OhData;

/// <summary>
/// The system query options a Priority-1 profile declares it honours (#475).
/// </summary>
/// <remarks>
/// <para>
/// Every other route shape has a fixed, framework-owned set of options it implements, and refuses
/// the rest by the sigil rule (#359/#380/#353). The Priority-1 route is the one shape where the
/// framework cannot know the answer: the profile receives the whole
/// <c>ODataQueryOptions&lt;TModel&gt;</c> and interprets it, so "the framework does not read this
/// option" is not "the request does not honour it". The profile therefore says.
/// </para>
/// <para>
/// <c>$format</c> is deliberately absent and always accepted: §11.2.10 negotiation is implemented
/// once on the group filter wrapping the whole surface, never reaches a route handler, and cannot
/// change a row.
/// </para>
/// </remarks>
[Flags]
public enum OhDataSystemQueryOption
{
    /// <summary>No system query option is honoured; every one is refused with <c>501</c>.</summary>
    None = 0,

    /// <summary><c>$filter</c>.</summary>
    Filter = 1 << 0,

    /// <summary><c>$orderby</c>.</summary>
    OrderBy = 1 << 1,

    /// <summary><c>$top</c>.</summary>
    Top = 1 << 2,

    /// <summary><c>$skip</c>.</summary>
    Skip = 1 << 3,

    /// <summary><c>$select</c>.</summary>
    Select = 1 << 4,

    /// <summary><c>$expand</c>.</summary>
    Expand = 1 << 5,

    /// <summary><c>$count</c>.</summary>
    Count = 1 << 6,

    /// <summary><c>$skiptoken</c>.</summary>
    SkipToken = 1 << 7,

    /// <summary>
    /// <c>$search</c>. Not in <see cref="Default"/>, because
    /// <c>ODataQueryOptions.ApplyTo</c> does NOT apply it: <c>SearchQueryOption.ApplyTo</c> returns
    /// the query untouched when no <c>ISearchBinder</c> is registered — *"If the developer doesn't
    /// provide the search binder, let's ignore the $search clause"* — so a profile that merely calls
    /// <c>ApplyTo</c> silently serves an unfiltered collection to a client that asked for a subset.
    /// Declare it only if the handler really interprets <c>options.Search</c> itself.
    /// </summary>
    Search = 1 << 8,

    /// <summary>
    /// What <c>ODataQueryOptions.ApplyTo</c> actually honours, and the default — so a profile that
    /// simply calls <c>ApplyTo</c> declares the truth without enumerating anything, and
    /// <see cref="Search"/> is refused rather than silently dropped.
    /// </summary>
    Default = Filter | OrderBy | Top | Skip | Select | Expand | Count | SkipToken,

    /// <summary>Every option this enum names, <see cref="Search"/> included.</summary>
    All = Default | Search,
}
