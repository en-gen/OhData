using System.Linq.Expressions;
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
        string? Filter   = null,
        string? Select   = null,
        string? OrderBy  = null,
        string? Expand   = null,
        int?    Top      = null,
        int?    Skip     = null);

    private readonly ODataHttpClient    _http;
    private readonly OhDataClientOptions _options;
    private readonly string             _entitySetName;
    private readonly QueryState         _state;

    internal EntitySetClient(ODataHttpClient http, OhDataClientOptions options, string entitySetName)
        : this(http, options, entitySetName, new QueryState()) { }

    private EntitySetClient(ODataHttpClient http, OhDataClientOptions options, string entitySetName, QueryState state)
    {
        _http          = http;
        _options       = options;
        _entitySetName = entitySetName;
        _state         = state;
    }

    // ── Builder methods ─────────────────────────────────────────────────────────

    /// <summary>Filters the collection using an expression predicate.</summary>
    /// <example><code>
    /// .Filter(x => x.Price > 10 &amp;&amp; x.Name.StartsWith("W"))
    /// </code></example>
    public EntitySetClient<T> Filter(Expression<Func<T, bool>> predicate)
        => With(_state with { Filter = FilterTranslator.Translate(predicate) });

    /// <summary>Filters using a raw OData <c>$filter</c> string.</summary>
    public EntitySetClient<T> Filter(string rawFilter)
        => With(_state with { Filter = rawFilter });

    /// <summary>Projects the response to a subset of properties.</summary>
    /// <example><code>
    /// .Select(x => new { x.Id, x.Name })
    /// </code></example>
    public EntitySetClient<T> Select(Expression<Func<T, object?>> selector)
        => With(_state with { Select = SelectTranslator.Translate(selector) });

    /// <summary>Projects the response to a subset of properties by name.</summary>
    public EntitySetClient<T> Select(params string[] properties)
        => With(_state with { Select = string.Join(',', properties) });

    /// <summary>Expands a navigation property.</summary>
    /// <example><code>.Expand(x => x.Category)</code></example>
    public EntitySetClient<T> Expand(Expression<Func<T, object?>> navProperty)
        => With(_state with { Expand = ExtractPath(navProperty) });

    /// <summary>Expands navigation properties by name.</summary>
    public EntitySetClient<T> Expand(params string[] navProperties)
        => With(_state with { Expand = string.Join(',', navProperties) });

    /// <summary>Sets the primary ascending sort.</summary>
    public EntitySetClient<T> OrderBy(Expression<Func<T, object?>> keySelector)
        => With(_state with { OrderBy = ExtractPath(keySelector) });

    /// <summary>Sets the primary descending sort.</summary>
    public EntitySetClient<T> OrderByDescending(Expression<Func<T, object?>> keySelector)
        => With(_state with { OrderBy = $"{ExtractPath(keySelector)} desc" });

    /// <summary>Appends a secondary ascending sort.</summary>
    public EntitySetClient<T> ThenBy(Expression<Func<T, object?>> keySelector)
    {
        var path = ExtractPath(keySelector);
        return With(_state with
        {
            OrderBy = _state.OrderBy is null ? path : $"{_state.OrderBy},{path}"
        });
    }

    /// <summary>Appends a secondary descending sort.</summary>
    public EntitySetClient<T> ThenByDescending(Expression<Func<T, object?>> keySelector)
    {
        var path = $"{ExtractPath(keySelector)} desc";
        return With(_state with
        {
            OrderBy = _state.OrderBy is null ? path : $"{_state.OrderBy},{path}"
        });
    }

    /// <summary>Limits the number of results returned.</summary>
    public EntitySetClient<T> Top(int count) => With(_state with { Top = count });

    /// <summary>Skips the first <paramref name="count"/> results (for paging).</summary>
    public EntitySetClient<T> Skip(int count) => With(_state with { Skip = count });

    // ── Key transition ──────────────────────────────────────────────────────────

    /// <summary>
    /// Transitions to a single-entity builder for the given key.
    /// </summary>
    public KeyedEntitySetClient<T> Key(object keyValue)
        => new(_http, _options, _entitySetName, ODataKeyFormatter.Format(keyValue));

    // ── Terminal operations ─────────────────────────────────────────────────────

    /// <summary>Executes GET and returns all matching entities.</summary>
    public Task<List<T>> ToListAsync(CancellationToken ct = default)
        => _http.GetCollectionAsync<T>(BuildCollectionUrl(), ct);

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
    /// POST a new entity. Returns the created entity as returned by the server
    /// (including any server-assigned key or computed fields).
    /// </summary>
    public Task<T> InsertAsync(T entity, CancellationToken ct = default)
        => _http.PostAsync(_entitySetName, entity, ct);

    // ── URL building (internal for testing) ────────────────────────────────────

    internal string BuildCollectionUrl()
    {
        var parts = new List<string>(6);
        if (_state.Filter  is not null) parts.Add($"$filter={Uri.EscapeDataString(_state.Filter)}");
        if (_state.Select  is not null) parts.Add($"$select={Uri.EscapeDataString(_state.Select)}");
        if (_state.OrderBy is not null) parts.Add($"$orderby={Uri.EscapeDataString(_state.OrderBy)}");
        if (_state.Expand  is not null) parts.Add($"$expand={Uri.EscapeDataString(_state.Expand)}");
        if (_state.Top.HasValue)        parts.Add($"$top={_state.Top.Value}");
        if (_state.Skip.HasValue)       parts.Add($"$skip={_state.Skip.Value}");

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
            body = u.Operand;

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
}
