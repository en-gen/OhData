using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OhData.Client.Internal;

namespace OhData.Client;

/// <summary>
/// Immutable fluent query builder for an OData entity set.
/// Each builder method returns a <em>new</em> instance — partial queries can be
/// safely stored and reused without side effects.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public sealed class EntitySetClient<T> where T : class
{
    // ── Immutable state ─────────────────────────────────────────────────────────

    private sealed record QueryState(
        string? Filter = null,
        string? Select = null,
        string? OrderBy = null,
        string? Expand = null,
        int? Top = null,
        int? Skip = null,
        bool WithCount = false);

    private readonly ODataHttpClient _http;
    private readonly OhDataClientOptions _options;
    private readonly string _entitySetName;
    private readonly QueryState _state;

    internal EntitySetClient(ODataHttpClient http, OhDataClientOptions options, string entitySetName)
        : this(http, options, entitySetName, new QueryState()) { }

    private EntitySetClient(ODataHttpClient http, OhDataClientOptions options, string entitySetName, QueryState state)
    {
        _http = http;
        _options = options;
        _entitySetName = entitySetName;
        _state = state;
    }

    // ── Builder methods ─────────────────────────────────────────────────────────

    /// <summary>Filters the collection using an expression predicate.</summary>
    /// <example><code>
    /// .Filter(x => x.Price > 10 &amp;&amp; x.Name.StartsWith("W"))
    /// </code></example>
    public EntitySetClient<T> Filter(Expression<Func<T, bool>> predicate)
    {
        string newFilter = FilterTranslator.Translate(predicate, _options.JsonOptions.PropertyNamingPolicy);
        string composed = _state.Filter is null ? newFilter : $"({_state.Filter}) and ({newFilter})";
        return With(_state with { Filter = composed });
    }

    /// <summary>Filters using a raw OData <c>$filter</c> string.</summary>
    public EntitySetClient<T> Filter(string rawFilter)
    {
        string composed = _state.Filter is null ? rawFilter : $"({_state.Filter}) and ({rawFilter})";
        return With(_state with { Filter = composed });
    }

    /// <summary>Projects the response to a subset of properties.</summary>
    /// <example><code>
    /// .Select(x => new { x.Id, x.Name })
    /// </code></example>
    public EntitySetClient<T> Select(Expression<Func<T, object?>> selector)
    {
        string newSelect = SelectTranslator.Translate(selector, _options.JsonOptions.PropertyNamingPolicy);
        string composed = _state.Select is null ? newSelect : $"{_state.Select},{newSelect}";
        return With(_state with { Select = composed });
    }

    /// <summary>
    /// Projects the response to a subset of properties using typed member-access expressions.
    /// Each expression must be a direct (non-chained) member access on <typeparamref name="T"/>
    /// (e.g. <c>x => x.Id</c>). Navigation paths such as <c>x => x.Category.Name</c> are
    /// not supported here — use <see cref="Expand(Expression{Func{T, object?}}[])"/> for
    /// navigation properties.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when any expression is not a direct member access on <typeparamref name="T"/>.
    /// </exception>
    public EntitySetClient<T> Select(params Expression<Func<T, object?>>[] properties)
    {
        string[] names = new string[properties.Length];
        for (int i = 0; i < properties.Length; i++)
        {
            names[i] = ExtractDirectMember(properties[i],
                "$select does not support navigation paths; use Expand for navigation properties.");
        }

        string newSelect = string.Join(',', names);
        string composed = _state.Select is null ? newSelect : $"{_state.Select},{newSelect}";
        return With(_state with { Select = composed });
    }

    /// <summary>
    /// Projects the response to a subset of properties by name.
    /// Property names must exactly match the server-side property names (case-sensitive on most OData servers).
    /// Passing an empty array produces an empty <c>$select=</c> parameter — prefer omitting <c>Select</c> entirely
    /// when no projection is desired.
    /// </summary>
    /// <example><code>
    /// .Select("Id", "Name", "Price")
    /// </code></example>
    public EntitySetClient<T> Select(params string[] properties)
    {
        string newSelect = string.Join(',', properties);
        string composed = _state.Select is null ? newSelect : $"{_state.Select},{newSelect}";
        return With(_state with { Select = composed });
    }

    /// <summary>Expands a navigation property.</summary>
    /// <example><code>.Expand(x => x.Category)</code></example>
    public EntitySetClient<T> Expand(Expression<Func<T, object?>> navProperty)
    {
        string newExpand = ExtractDirectMember(navProperty,
            "$expand does not support chained navigation paths; use the string overload for complex $expand syntax.");
        string composed = _state.Expand is null ? newExpand : $"{_state.Expand},{newExpand}";
        return With(_state with { Expand = composed });
    }

    /// <summary>
    /// Expands multiple navigation properties using typed member-access expressions.
    /// Each expression must be a direct (non-chained) member access on <typeparamref name="T"/>
    /// (e.g. <c>x => x.Category</c>). For complex nested OData <c>$expand</c> syntax
    /// (e.g. <c>Category($select=Name)</c>) use the string overload instead.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when any expression is not a direct member access on <typeparamref name="T"/>.
    /// </exception>
    public EntitySetClient<T> Expand(params Expression<Func<T, object?>>[] navProperties)
    {
        string[] names = new string[navProperties.Length];
        for (int i = 0; i < navProperties.Length; i++)
        {
            names[i] = ExtractDirectMember(navProperties[i],
                "$expand does not support nested expansion; use the string overload for complex $expand syntax.");
        }

        string newExpand = string.Join(',', names);
        string composed = _state.Expand is null ? newExpand : $"{_state.Expand},{newExpand}";
        return With(_state with { Expand = composed });
    }

    /// <summary>
    /// Expands navigation properties by name. Supports complex nested OData <c>$expand</c> syntax
    /// (e.g. <c>"Category($select=Name)"</c>) that the typed-expression overloads do not support.
    /// </summary>
    /// <example><code>
    /// .Expand("Category", "Tags")
    /// // Complex nested expand:
    /// .Expand("Category($select=Name;$expand=Parent)")
    /// </code></example>
    public EntitySetClient<T> Expand(params string[] navProperties)
    {
        string newExpand = string.Join(',', navProperties);
        string composed = _state.Expand is null ? newExpand : $"{_state.Expand},{newExpand}";
        return With(_state with { Expand = composed });
    }

    /// <summary>Sets the primary ascending sort.</summary>
    public EntitySetClient<T> OrderBy(Expression<Func<T, object?>> keySelector)
        => With(_state with { OrderBy = ExtractPath(keySelector) });

    /// <summary>Sets the primary descending sort.</summary>
    public EntitySetClient<T> OrderByDescending(Expression<Func<T, object?>> keySelector)
        => With(_state with { OrderBy = $"{ExtractPath(keySelector)} desc" });

    /// <summary>Appends a secondary ascending sort.</summary>
    public EntitySetClient<T> ThenBy(Expression<Func<T, object?>> keySelector)
    {
        string path = ExtractPath(keySelector);
        return With(_state with
        {
            OrderBy = _state.OrderBy is null ? path : $"{_state.OrderBy},{path}"
        });
    }

    /// <summary>Appends a secondary descending sort.</summary>
    public EntitySetClient<T> ThenByDescending(Expression<Func<T, object?>> keySelector)
    {
        string path = $"{ExtractPath(keySelector)} desc";
        return With(_state with
        {
            OrderBy = _state.OrderBy is null ? path : $"{_state.OrderBy},{path}"
        });
    }

    /// <summary>Limits the number of results returned.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count"/> is negative.</exception>
    public EntitySetClient<T> Top(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "$top must be >= 0.");
        return With(_state with { Top = count });
    }

    /// <summary>Skips the first <paramref name="count"/> results (for paging).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count"/> is negative.</exception>
    public EntitySetClient<T> Skip(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "$skip must be >= 0.");
        return With(_state with { Skip = count });
    }

    /// <summary>
    /// Requests an inline total count from the server by appending <c>$count=true</c>.
    /// The count is available on the <see cref="ODataPage{T}.TotalCount"/> property
    /// of the result returned by <see cref="ToPageAsync"/>.
    /// </summary>
    public EntitySetClient<T> IncludeCount() => With(_state with { WithCount = true });

    // ── Key transition ──────────────────────────────────────────────────────────

    /// <summary>
    /// Transitions to a single-entity builder for the given key.
    /// </summary>
    public KeyedEntitySetClient<T> Key(object keyValue)
        => new(_http, _options, _entitySetName, ODataKeyFormatter.Format(keyValue), _state.Select, _state.Expand);

    /// <summary>
    /// Transitions to a single-entity builder for the given key.
    /// This overload provides compile-time type safety for the key value.
    /// </summary>
    public KeyedEntitySetClient<T> Key<TKey>(TKey keyValue)
        => Key((object)keyValue!);

    // ── Terminal operations ─────────────────────────────────────────────────────

    /// <summary>
    /// Lazily fetches all pages by following <c>@odata.nextLink</c>, yielding each entity
    /// as it arrives. The first page is fetched using the current query state; subsequent
    /// pages are fetched directly from the server-provided nextLink URL without re-applying
    /// any client-side query parameters.
    /// </summary>
    public async IAsyncEnumerable<T> ToAsyncEnumerable(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ODataPage<T> page = await _http.GetPageAsync<T>(BuildCollectionUrl(), ct);
        foreach (T item in page.Items)
            yield return item;

        while (page.NextLink is not null)
        {
            page = await _http.GetPageByAbsoluteUrlAsync<T>(page.NextLink, ct);
            foreach (T item in page.Items)
                yield return item;
        }
    }

    /// <summary>Executes GET and returns all matching entities, following all nextLinks.</summary>
    public async Task<List<T>> ToListAsync(CancellationToken ct = default)
    {
        var result = new List<T>();
        await foreach (T item in ToAsyncEnumerable(ct))
            result.Add(item);
        return result;
    }

    /// <summary>
    /// Executes GET with <c>$top=1</c> and returns the first result, or
    /// <see langword="null"/> when the collection is empty.
    /// </summary>
    public async Task<T?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        var items = await With(_state with { Top = 1 })
            .ToListAsync(ct);
        return items.Count > 0 ? items[0] : null;
    }

    /// <summary>Executes GET <c>/$count</c> and returns the matching entity count.</summary>
    public Task<long> CountAsync(CancellationToken ct = default)
        => _http.GetCountAsync(BuildCountUrl(), ct);

    /// <summary>
    /// Returns <see langword="true"/> when at least one entity matches the current query options;
    /// <see langword="false"/> otherwise. Executes GET <c>/$count</c>.
    /// </summary>
    public async Task<bool> AnyAsync(CancellationToken ct = default)
        => await CountAsync(ct) > 0;

    /// <summary>
    /// Executes GET with <c>$count=true</c> and returns both the items and the total
    /// matching count (before any <c>$top</c>/<c>$skip</c>).
    /// </summary>
    public Task<ODataPage<T>> ToPageAsync(CancellationToken ct = default)
        => _http.GetPageAsync<T>(With(_state with { WithCount = true }).BuildCollectionUrl(), ct);

    /// <summary>
    /// POST a new entity. Returns the created entity as returned by the server
    /// (including any server-assigned key or computed fields), or <see langword="null"/>
    /// when the server responds with 204 No Content (Prefer: return=minimal).
    /// </summary>
    /// <remarks>
    /// Any query options set on the builder (Filter, Select, OrderBy, etc.) are ignored
    /// for POST -- the request always targets the bare entity set URL.
    /// </remarks>
    public Task<T?> InsertAsync(T entity, bool preferMinimal = false, CancellationToken ct = default)
        => _http.PostAsync(_entitySetName, entity, preferMinimal, ct);

    /// <summary>
    /// Executes GET with <c>$top=2</c> and returns the single matching entity,
    /// or <see langword="null"/> when none match. Throws <see cref="InvalidOperationException"/>
    /// when more than one entity matches (use <see cref="FirstOrDefaultAsync"/> if you expect multiple).
    /// </summary>
    public async Task<T?> SingleOrDefaultAsync(CancellationToken ct = default)
    {
        var items = await With(_state with { Top = 2 }).ToListAsync(ct);
        return items.Count switch
        {
            0 => null,
            1 => items[0],
            _ => throw new InvalidOperationException(
                "Sequence contains more than one element matching the query. Use FirstOrDefaultAsync if multiple results are expected.")
        };
    }

    /// <summary>Executes GET and returns all matching entities as an array.</summary>
    public async Task<T[]> ToArrayAsync(CancellationToken ct = default)
        => [.. await ToListAsync(ct)];

    /// <summary>
    /// Executes GET with <c>$top=1</c> and returns the first result.
    /// Throws <see cref="InvalidOperationException"/> when the collection is empty
    /// (use <see cref="FirstOrDefaultAsync"/> if the collection may be empty).
    /// </summary>
    public async Task<T> FirstAsync(CancellationToken ct = default)
    {
        T? result = await FirstOrDefaultAsync(ct);
        return result ?? throw new InvalidOperationException(
            "Sequence contains no elements matching the query.");
    }

    /// <summary>
    /// Executes GET with <c>$top=2</c> and returns the single matching entity.
    /// Throws <see cref="InvalidOperationException"/> when no entity or more than one entity matches.
    /// Use <see cref="SingleOrDefaultAsync"/> if zero results is a valid outcome.
    /// </summary>
    public async Task<T> SingleAsync(CancellationToken ct = default)
    {
        T? result = await SingleOrDefaultAsync(ct);
        return result ?? throw new InvalidOperationException(
            "Sequence contains no elements matching the query.");
    }

    // ── URL building (internal for testing) ────────────────────────────────────

    internal string BuildCollectionUrl()
    {
        // Fast path: no query options — avoids allocating the parts list.
        if (_state.Filter is null &&
            _state.Select is null &&
            _state.OrderBy is null &&
            _state.Expand is null &&
            !_state.Top.HasValue &&
            !_state.Skip.HasValue &&
            !_state.WithCount)
        {
            return _entitySetName;
        }

        var parts = new List<string>(6);
        if (_state.Filter is not null) parts.Add($"$filter={Uri.EscapeDataString(_state.Filter)}");
        if (_state.Select is not null) parts.Add($"$select={Uri.EscapeDataString(_state.Select)}");
        if (_state.OrderBy is not null) parts.Add($"$orderby={Uri.EscapeDataString(_state.OrderBy)}");
        if (_state.Expand is not null) parts.Add($"$expand={Uri.EscapeDataString(_state.Expand)}");
        if (_state.Top.HasValue) parts.Add($"$top={_state.Top.Value}");
        if (_state.Skip.HasValue) parts.Add($"$skip={_state.Skip.Value}");
        if (_state.WithCount) parts.Add("$count=true");

        return parts.Count == 0
            ? _entitySetName
            : $"{_entitySetName}?{string.Join('&', parts)}";
    }

    internal string BuildCountUrl()
    {
        if (_state.Filter is null) return $"{_entitySetName}/$count";
        return $"{_entitySetName}/$count?$filter={Uri.EscapeDataString(_state.Filter)}";
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private EntitySetClient<T> With(QueryState state) =>
        new(_http, _options, _entitySetName, state);

    /// <summary>
    /// Extracts an OData property path from a member-access lambda,
    /// stripping boxing <c>Convert</c> wrappers that appear when the lambda
    /// returns <c>object?</c>.
    /// </summary>
    private static string ExtractPath(Expression<Func<T, object?>> expr)
    {
        Expression body = expr.Body;
        while (body is UnaryExpression u
            && u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            body = u.Operand;
        }

        return ExtractMemberPath(body, expr.Parameters[0]);
    }

    private static string ExtractMemberPath(Expression expr, ParameterExpression param)
    {
        if (expr is MemberExpression member)
        {
            if (member.Expression is ParameterExpression p && p == param)
                return member.Member.Name;

            if (member.Expression is not null)
                return $"{ExtractMemberPath(member.Expression, param)}/{member.Member.Name}";
        }

        throw new ArgumentException(
            $"Expected a property-access expression (e.g. x => x.Name), got: '{expr}'.");
    }

    /// <summary>
    /// Extracts the name of a single direct member of <typeparamref name="T"/> from
    /// <paramref name="expr"/>. Throws <see cref="ArgumentException"/> (with
    /// <paramref name="errorMessage"/>) when the expression is chained
    /// (e.g. <c>x => x.Category.Name</c>) rather than a direct access
    /// (e.g. <c>x => x.Id</c>).
    /// </summary>
    private static string ExtractDirectMember(Expression<Func<T, object?>> expr, string errorMessage)
    {
        Expression body = expr.Body;
        while (body is UnaryExpression u
            && u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            body = u.Operand;
        }

        if (body is MemberExpression member
            && member.Expression is ParameterExpression p
            && p == expr.Parameters[0])
        {
            return member.Member.Name;
        }

        throw new ArgumentException(errorMessage);
    }
}
