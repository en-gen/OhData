using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.OData.ModelBuilder;

namespace OhData.Abstractions;

/// <summary>
/// Base class for defining an OData entity set. Derive from this class in your application
/// and assign the handler delegates (<see cref="GetAll"/>, <see cref="GetById"/>, etc.) inside
/// the constructor to enable the corresponding HTTP endpoints.
/// </summary>
/// <typeparam name="TKey">The CLR type of the entity's primary key.</typeparam>
/// <typeparam name="TModel">The CLR type of the entity.</typeparam>
public abstract class EntitySetProfile<TKey, TModel> : IEntitySetProfile, IVisitModelBuilder, IEntitySetEndpointSource
    where TModel : class
{
    // Caches the compiled ETag function per concrete type. The delegate accesses model
    // properties only (no DI dependencies), so it is safe to share across request scopes.
    private static readonly ConcurrentDictionary<Type, Func<TModel, string>> s_etagCache = new();

    // Caches the compiled key-to-string delegate per concrete type. Expression.Compile() is
    // expensive (~100μs); caching avoids per-request compilation under scoped resolution.
    private static readonly ConcurrentDictionary<Type, Func<TModel, string>> s_keyToStringCache = new();

    // Caches compiled structural-property accessor delegates keyed by property name. Shared
    // across every concrete profile subclass that closes EntitySetProfile over the same TModel
    // (the property set is a pure function of TModel, not of the subclass), so a single
    // Expression.Compile() per property name suffices for the process lifetime.
    private static readonly ConcurrentDictionary<string, Func<TModel, object?>> s_structuralAccessorCache = new();

    // OData primitive CLR types (Part 3 §7.1 Edm primitive type mapping). Anything outside this
    // set (after unwrapping Nullable<T>) is treated as "complex" for /$value purposes — it has
    // no raw-value representation (OData Part 2 §4.7).
    private static readonly HashSet<Type> s_primitiveClrTypes = new()
    {
        typeof(string), typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
        typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double),
        typeof(decimal), typeof(Guid), typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly),
        typeof(TimeOnly), typeof(TimeSpan), typeof(char), typeof(byte[]),
    };

    // Names of properties declared as navigation properties via HasOptional/HasRequired/HasMany
    // (any overload — all funnel through the single-argument base method). Structural properties
    // are computed as "typeof(TModel) public readable properties minus this set", so structural
    // and navigation routes are disjoint by construction (never both claim the same route).
    private readonly HashSet<string> _navigationPropertyNames = new(StringComparer.Ordinal);

    // Names of properties excluded from the OData surface via Ignore() (#226). Structural
    // properties, the EDM, response serialization, and request binding all consult this set.
    private readonly HashSet<string> _ignoredPropertyNames = new(StringComparer.Ordinal);

    // The open generic StructuralTypeConfiguration<TModel>.Ignore<TProperty>(...) definition,
    // closed per ignored property with its real CLR type (see Ignore below).
    private static readonly MethodInfo s_edmIgnoreMethodDefinition =
        typeof(StructuralTypeConfiguration<TModel>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == "Ignore" && m.IsGenericMethodDefinition);

    private readonly Expression<Func<TModel, TKey>> _getKey;

    /// <summary>
    /// The OData entity set name used in URL routes and <c>$metadata</c>. Defaults to
    /// <c>"{ModelTypeName}s"</c>. Override in the derived constructor if a different name is needed:
    /// <c>EntitySetName = "MyWidgets";</c>
    /// </summary>
    protected string EntitySetName { get; init; }

    /// <summary>
    /// Controls whether <c>$select</c> is allowed on this entity set (OData §11.2.4.1).
    /// Inherits from <see cref="EntitySetDefaults"/> when <c>null</c> (the default).
    /// </summary>
    protected bool? SelectEnabled { get; init; }

    /// <summary>
    /// Controls whether <c>$expand</c> is allowed on this entity set (OData §11.2.4.2).
    /// Inherits from <see cref="EntitySetDefaults"/> when <c>null</c> (the default).
    /// </summary>
    protected bool? ExpandEnabled { get; init; }

    /// <summary>
    /// Controls whether <c>$filter</c> is allowed on this entity set (OData §11.2.6.1).
    /// Inherits from <see cref="EntitySetDefaults"/> when <c>null</c> (the default).
    /// </summary>
    protected bool? FilterEnabled { get; init; }

    /// <summary>
    /// Controls whether <c>$orderby</c> is allowed on this entity set (OData §11.2.6.2).
    /// Inherits from <see cref="EntitySetDefaults"/> when <c>null</c> (the default).
    /// </summary>
    protected bool? OrderByEnabled { get; init; }

    /// <summary>
    /// Controls whether <c>$count</c> is allowed on this entity set (OData §11.2.6.5).
    /// Inherits from <see cref="EntitySetDefaults"/> when <c>null</c> (the default).
    /// </summary>
    protected bool? CountEnabled { get; init; }

    /// <summary>
    /// Controls whether individual structural property access is enabled on this entity set
    /// (<c>GET /{EntitySet}({key})/{Property}</c> and <c>GET .../{Property}/$value</c>,
    /// OData §11.2.6 / Part 2 §4.6-4.7). Inherits from <see cref="EntitySetDefaults"/>
    /// (default <c>true</c>) when <c>null</c>. Property routes are registered only when this
    /// resolves <c>true</c> AND <see cref="GetById"/> is configured — property reads ride the
    /// existing <c>GetById</c> handler.
    /// </summary>
    protected bool? PropertyAccessEnabled { get; init; }

    /// <summary>
    /// Controls whether <c>$select</c> projection pushdown applies on this entity set's
    /// <c>GetQueryable</c> path (#206). When enabled and a request's <c>$select</c> is
    /// eligible, the framework composes a member-init projection onto the queryable so the
    /// LINQ provider emits a column-pruned <c>SELECT</c>; wire output is byte-identical either
    /// way. Inherits from <see cref="EntitySetDefaults"/> (default <c>true</c>) when
    /// <c>null</c>. Disable for <c>IQueryable</c> providers that cannot translate member-init.
    /// </summary>
    protected bool? SelectPushdownEnabled { get; init; }

    /// <summary>
    /// Controls whether <c>$expand</c> Include pushdown applies on this entity set's
    /// <c>GetQueryable</c> path (#206 phase 2). When enabled and a request's top-level
    /// <c>$expand</c> names a navigation declared <b>without</b> a custom expand delegate (a bare
    /// <see cref="HasMany{T}(System.Linq.Expressions.Expression{System.Func{TModel, System.Collections.Generic.IEnumerable{T}}})"/>
    /// / <c>HasOptional</c> / <c>HasRequired</c>), the framework folds that navigation into the
    /// collection query's projection so an EF Core-backed source loads the related rows via a
    /// single JOIN'd query instead of leaving the navigation unexpandable. The expand's nested
    /// options are honored: <c>$filter</c>/<c>$orderby</c>/<c>$top</c>/<c>$skip</c> push to SQL as a
    /// filtered/ordered/paged <c>Include</c>, and <c>$count</c>/<c>$select</c> shape the result;
    /// a nested <c>$expand</c> (multi-level) or <c>$levels</c> is not pushed (see docs/query-options.md).
    /// A navigation declared <b>with</b> a delegate always expands through its delegate and is never
    /// pushed down. Inherits from <see cref="EntitySetDefaults"/> (default <c>true</c>) when <c>null</c>.
    /// Disable to keep every delegate-less navigation unexpandable.
    /// </summary>
    protected bool? ExpandPushdownEnabled { get; init; }

    /// <summary>
    /// Controls whether this entity set's structural property routes
    /// (<c>GET /{EntitySet}({key})/{Property}</c>, <c>.../{Property}/$value</c>, and the
    /// <c>PUT</c>/<c>PATCH</c>/<c>DELETE</c> property writes) appear in the generated API
    /// documentation. Inherits from <see cref="EntitySetDefaults"/> (default <c>false</c>) when
    /// <c>null</c>. Documentation-only: property routes remain live at runtime whenever
    /// <see cref="PropertyAccessEnabled"/> resolves <c>true</c> and the required handler is
    /// configured, regardless of this flag.
    /// </summary>
    protected bool? PropertyRouteDocsEnabled { get; init; }

    /// <summary>
    /// Controls the midpoint-rounding behavior of the OData <c>round()</c> canonical function
    /// (Part 2 §5.1.1.9) on the <c>GetQueryable</c> pushdown path. Inherits from
    /// <c>EntitySetDefaults.RoundingMode</c> (default
    /// <c>OhData.Abstractions.RoundingMode.SpecCompliant</c>) when <c>null</c>. See
    /// <c>OhData.Abstractions.RoundingMode</c> for the EF Core provider-translation caveat that
    /// motivates <c>OhData.Abstractions.RoundingMode.BankersRounding</c>.
    /// <para>
    /// Only reaches the base-class <c>GetQueryable</c> path (and its <c>$count</c> companion),
    /// where the framework owns the <c>ApplyTo</c> call. On the Priority-1
    /// <c>ODataEntitySetProfile.GetODataQueryable</c> path the profile calls <c>ApplyTo</c>
    /// itself, so this setting does not automatically apply there — read the resolved value
    /// yourself if you need the same rewrite in a custom <c>GetODataQueryable</c> handler.
    /// </para>
    /// </summary>
    protected RoundingMode? RoundingMode { get; init; }
    private RoundingMode _resolvedRoundingMode;

    /// <summary>
    /// Controls whether <c>POST /{EntitySet}</c> passes nested navigation-property values
    /// through to the <see cref="Post"/> handler (deep insert, OData §11.4.2.2). Inherits from
    /// <see cref="EntitySetDefaults.AllowDeepInsert"/> (default <c>false</c>) when <c>null</c>.
    /// <para>
    /// <c>false</c> (default): nested navigation values (single-valued or collection) are set
    /// to <c>null</c> on the deserialized model before <see cref="Post"/> is invoked — a
    /// <c>Post</c> handler that doesn't expect a graph never silently persists only part of it.
    /// </para>
    /// <para>
    /// <c>true</c>: the full deserialized graph (parent + nested navigation values) is passed
    /// to <see cref="Post"/> as-is. The handler is contractually responsible for persisting the
    /// whole graph atomically (e.g. one EF Core <c>SaveChanges</c>) — the framework does not
    /// open a transaction on the handler's behalf.
    /// </para>
    /// <para>
    /// <c>prop@odata.bind</c> annotations (linking to an existing entity instead of creating a
    /// new one, JSON format §8.5) are not supported in either mode: a request body containing
    /// one is rejected with <c>501 Not Implemented</c>. Use the <c>$ref</c> endpoints to link
    /// existing entities instead.
    /// </para>
    /// </summary>
    protected bool? AllowDeepInsert { get; init; }
    private bool _resolvedAllowDeepInsert;

    private string[]? _selectProperties;
    private string[]? _expandProperties;
    private string[]? _filterProperties;
    private string[]? _orderByProperties;

    /// <summary>
    /// Registers the <c>GET /{EntitySet}</c> handler (OData §11.2.1 — Requesting a Collection).
    /// When set, the framework returns the full enumerable as-is with no query options applied.
    /// Use <see cref="GetQueryable"/> instead when the data source can push <c>$filter</c>,
    /// <c>$orderby</c>, <c>$skip</c>, and <c>$top</c> to the database.
    /// </summary>
    /// <remarks>
    /// Leaving this <c>null</c> (the default) means no <c>GET /{EntitySet}</c> route is registered,
    /// unless <see cref="GetQueryable"/> is set.
    /// </remarks>
    protected Func<CancellationToken, Task<IEnumerable<TModel>>>? GetAll = null;

    /// <summary>
    /// Registers the <c>GET /{EntitySet}</c> handler using an <see cref="IQueryable{T}"/> source
    /// (OData §11.2.1 — Requesting a Collection). The framework applies <c>$filter</c>,
    /// <c>$orderby</c>, <c>$skip</c>, and <c>$top</c> via <c>ApplyTo</c>, enabling full SQL
    /// pushdown when backed by EF Core. <c>$select</c> is applied via JSON post-processing
    /// to preserve the configured JSON naming policy (PascalCase by default).
    /// </summary>
    /// <remarks>
    /// Leaving this <c>null</c> (the default) means no <c>GET /{EntitySet}</c> route is registered,
    /// unless <see cref="GetAll"/> is set. Takes priority over <see cref="GetAll"/> when both are set.
    /// </remarks>
    protected Func<CancellationToken, Task<IQueryable<TModel>>>? GetQueryable = null;

    /// <summary>
    /// Registers the <c>GET /{EntitySet}({key})</c> handler (OData §11.2.2 — Requesting an Entity).
    /// Return <c>null</c> to produce a <c>404 Not Found</c> response per §9.1.4.
    /// </summary>
    /// <remarks>
    /// Leaving this <c>null</c> (the default) means no <c>GET /{EntitySet}({key})</c> route is registered.
    /// </remarks>
    protected Func<TKey, CancellationToken, Task<TModel?>>? GetById = null;

    /// <summary>
    /// Registers the <c>PUT /{EntitySet}({key})</c> handler (OData §11.4.3 — Update an Entity).
    /// Return <c>null</c> to produce a <c>404 Not Found</c> response per §9.1.4.
    /// </summary>
    /// <remarks>
    /// Leaving this <c>null</c> (the default) means no <c>PUT /{EntitySet}({key})</c> route is registered.
    /// Set <see cref="AllowUpsert"/> to enable upsert semantics (§11.4.4) when the key does not exist.
    /// </remarks>
    protected Func<TKey, TModel, CancellationToken, Task<TModel>>? Put = null;

    /// <summary>
    /// Registers the <c>POST /{EntitySet}</c> handler (OData §11.4.1 — Create an Entity).
    /// Return <c>null</c> to produce a <c>400 Bad Request</c> response.
    /// </summary>
    /// <remarks>
    /// Leaving this <c>null</c> (the default) means no <c>POST /{EntitySet}</c> route is registered.
    /// </remarks>
    protected Func<TModel, CancellationToken, Task<TModel?>>? Post = null;

    /// <summary>
    /// Handler for PATCH /{EntitySet}({key}).
    /// The handler receives a <see cref="Delta{TModel}"/> containing only the properties
    /// present in the request body. Use <see cref="Delta{TModel}.GetChangedPropertyNames"/>
    /// to inspect which properties were sent, or call <c>delta.Patch(existingEntity)</c>
    /// to apply changed fields in-place. The handler is responsible for fetching the
    /// existing entity (if needed) and persisting the changes.
    /// Return <c>null</c> to produce a 404 Not Found response.
    /// </summary>
    protected Func<TKey, Delta<TModel>, CancellationToken, Task<TModel?>>? Patch = null;

    /// <summary>
    /// Registers the <c>DELETE /{EntitySet}({key})</c> handler (OData §11.4.5 — Delete an Entity).
    /// Return <c>false</c> to produce a <c>404 Not Found</c> response; return <c>true</c> for
    /// <c>204 No Content</c>.
    /// </summary>
    /// <remarks>
    /// Leaving this <c>null</c> (the default) means no <c>DELETE /{EntitySet}({key})</c> route is registered.
    /// Set <see cref="IdempotentDelete"/> to control the behaviour when the entity does not exist.
    /// </remarks>
    protected Func<TKey, CancellationToken, Task<bool>>? Delete = null;

    /// <summary>
    /// Registers a free-text search handler for <c>GET /{EntitySet}?$search=term</c>
    /// (OData §11.2.6.6 — System Query Option <c>$search</c>). The raw search term from the
    /// query string is passed to this delegate; return matching entities.
    /// </summary>
    /// <remarks>
    /// Leaving this <c>null</c> (the default) means <c>$search</c> requests return
    /// <c>400 Bad Request</c> with an <c>UnsupportedQueryOption</c> error.
    /// </remarks>
    protected Func<string, CancellationToken, Task<IEnumerable<TModel>>>? Search = null;

    private int? _maxTop;

    /// <summary>
    /// Maximum value the client may specify in <c>$top</c> (OData §11.2.6.3).
    /// The framework enforces this limit; requests exceeding it receive a
    /// <c>400 Bad Request</c>. Inherits from <see cref="EntitySetDefaults.MaxTop"/> when
    /// <c>null</c>. Must be a positive integer.
    /// </summary>
    protected int? MaxTop
    {
        get => _maxTop;
        init
        {
            if (value is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxTop), value, "MaxTop must be a positive integer or null.");
            _maxTop = value;
        }
    }
    private int? _resolvedMaxTop;

    private long? _maxRequestBodyBytes;

    /// <summary>
    /// Maximum request-body size, in bytes, for this entity set's write operations (POST/PUT/PATCH
    /// and their navigation/<c>$ref</c>/property/action variants). A request whose body exceeds this
    /// limit is rejected with <c>413 Payload Too Large</c> before deserialization. Inherits from
    /// <see cref="EntitySetDefaults.MaxRequestBodyBytes"/> when <c>null</c>; when both are <c>null</c>
    /// no OhData-level limit applies (the host's Kestrel <c>MaxRequestBodySize</c> still bounds it).
    /// Must be a positive value.
    /// </summary>
    protected long? MaxRequestBodyBytes
    {
        get => _maxRequestBodyBytes;
        init
        {
            if (value is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxRequestBodyBytes), value, "MaxRequestBodyBytes must be a positive value or null.");
            _maxRequestBodyBytes = value;
        }
    }
    private long? _resolvedMaxRequestBodyBytes;

    private int? _maxExpansionDepth;
    private int? _maxFilterNodeCount;
    private int? _maxOrderByNodeCount;
    private int? _maxAnyAllExpressionDepth;

    /// <summary>#202/#206: maximum nested <c>$expand</c> depth for this set, and the ceiling
    /// <c>$levels</c> is resolved/capped to (400 beyond it). Inherits
    /// <see cref="EntitySetDefaults.MaxExpansionDepth"/> (default 3) when null. Must be positive.</summary>
    protected int? MaxExpansionDepth
    {
        get => _maxExpansionDepth;
        init
        {
            if (value is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxExpansionDepth), value, "MaxExpansionDepth must be a positive integer.");
            _maxExpansionDepth = value;
        }
    }

    /// <summary>#202: maximum <c>$filter</c> node count for this set. Inherits
    /// <see cref="EntitySetDefaults.MaxFilterNodeCount"/> (default 10000) when null. Must be positive.</summary>
    protected int? MaxFilterNodeCount
    {
        get => _maxFilterNodeCount;
        init
        {
            if (value is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxFilterNodeCount), value, "MaxFilterNodeCount must be a positive integer.");
            _maxFilterNodeCount = value;
        }
    }

    /// <summary>#202: maximum <c>$orderby</c> node count for this set. Inherits
    /// <see cref="EntitySetDefaults.MaxOrderByNodeCount"/> (default 1000) when null. Must be positive.</summary>
    protected int? MaxOrderByNodeCount
    {
        get => _maxOrderByNodeCount;
        init
        {
            if (value is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxOrderByNodeCount), value, "MaxOrderByNodeCount must be a positive integer.");
            _maxOrderByNodeCount = value;
        }
    }

    /// <summary>#202: maximum <c>any()</c>/<c>all()</c> lambda nesting depth in a <c>$filter</c> for
    /// this set. Inherits <see cref="EntitySetDefaults.MaxAnyAllExpressionDepth"/> (default 1000) when
    /// null. Must be positive.</summary>
    protected int? MaxAnyAllExpressionDepth
    {
        get => _maxAnyAllExpressionDepth;
        init
        {
            if (value is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxAnyAllExpressionDepth), value, "MaxAnyAllExpressionDepth must be a positive integer.");
            _maxAnyAllExpressionDepth = value;
        }
    }
    private int _resolvedMaxExpansionDepth;
    private int _resolvedMaxFilterNodeCount;
    private int _resolvedMaxOrderByNodeCount;
    private int _resolvedMaxAnyAllExpressionDepth;
    private bool _resolvedFilterEnabled;
    private bool _resolvedOrderByEnabled;
    private bool _resolvedSelectEnabled;
    private bool _resolvedExpandEnabled;
    private bool _resolvedCountEnabled;
    private bool _resolvedPropertyAccessEnabled;
    private bool _resolvedSelectPushdownEnabled;
    private bool _resolvedExpandPushdownEnabled;
    private bool _resolvedPropertyRouteDocsEnabled;
    private List<StructuralPropertyInfo>? _structuralProperties;

    /// <summary>
    /// When <c>true</c>, <c>DELETE</c> on a non-existent resource returns <c>204 No Content</c>
    /// (idempotent semantics). When <c>false</c>, returns <c>404 Not Found</c>.
    /// Inherits from <see cref="EntitySetDefaults.IdempotentDelete"/> when <c>null</c>.
    /// </summary>
    /// <remarks>
    /// OData §11.4.5 permits either behaviour; this property selects which the profile uses.
    /// </remarks>
    protected bool? IdempotentDelete { get; init; }
    private bool _resolvedIdempotentDelete;

    /// <summary>
    /// When <c>true</c>, a <c>PUT</c> to a non-existent key creates the entity
    /// (upsert semantics, OData §11.4.4). Requires <see cref="Post"/> to also be configured.
    /// Inherits from <see cref="EntitySetDefaults.AllowUpsert"/> when <c>null</c>.
    /// </summary>
    protected bool? AllowUpsert { get; init; }
    private bool _resolvedAllowUpsert;
    private IReadOnlyList<BoundOperationDefinition>? _resolvedBoundFunctions;
    private IReadOnlyList<BoundOperationDefinition>? _resolvedBoundActions;
    private bool _isSealed;

    private Func<TModel, string>? _getETag;

    /// <summary>
    /// Opts in to ETag generation. The framework hashes the values of the specified
    /// properties using SHA-256 and encodes the result as Base64, returning it in the
    /// <c>ETag</c> response header (OData §8.2.6) and the <c>@odata.etag</c> annotation.
    /// <para>
    /// Supports <c>byte[]</c> values (e.g. row-version columns) directly;
    /// all other values are hashed as their UTF-8 string representations.
    /// </para>
    /// </summary>
    /// <remarks>
    /// When ETags are enabled the framework checks the <c>If-Match</c> request header on
    /// mutating operations (PUT, PATCH, DELETE) and returns <c>412 Precondition Failed</c>
    /// on mismatch (OData §8.2.5). GET responses support <c>If-None-Match</c> with
    /// <c>304 Not Modified</c> (OData §8.2.5).
    /// </remarks>
    /// <param name="propertySelectors">
    /// One or more property selectors whose values are combined into the ETag hash.
    /// At least one selector is required.
    /// </param>
    protected void UseETag(params Expression<Func<TModel, object?>>[] propertySelectors)
    {
        // #206: capture the ETag property NAMES (direct-member selectors only) for the
        // $select projection-pushdown eligibility check — the projection must include every
        // ETag input so @odata.etag values are identical with and without pushdown. Runs
        // BEFORE the compiled-delegate cache early return below, which would otherwise skip
        // this on every construction after the first. A selector that is not a direct member
        // access makes the names unknowable → null → pushdown ineligible while ETags are on.
        _etagPropertyNames = TryExtractDirectMemberNames(propertySelectors);

        // Reuse the cached compiled delegate if available (avoids recompiling on every scoped construction).
        if (s_etagCache.TryGetValue(GetType(), out var cached))
        {
            _getETag = cached;
            return;
        }

        ThrowIfSealed();
        if (propertySelectors.Length == 0)
            throw new ArgumentException("At least one property selector is required.", nameof(propertySelectors));
        var getters = propertySelectors.Select(e => e.Compile()).ToArray();
        byte[] sep = new byte[] { 0x00 };
        _getETag = model =>
        {
            // Collect all bytes into a buffer, then hash once without allocating a hasher object per call.
            using var ms = new MemoryStream();
            for (int i = 0; i < getters.Length; i++)
            {
                if (i > 0) ms.Write(sep, 0, sep.Length);
                object? value = getters[i](model);
                if (value is byte[] bytes)
                {
                    ms.Write(bytes, 0, bytes.Length);
                }
                else if (value is not null)
                {
                    byte[] strBytes = Encoding.UTF8.GetBytes(value.ToString()!);
                    ms.Write(strBytes, 0, strBytes.Length);
                }
            }
            // Use static SHA256.HashData to avoid per-call object allocation.
            if (!ms.TryGetBuffer(out ArraySegment<byte> buffer))
            {
                buffer = new ArraySegment<byte>(ms.ToArray());
            }
            byte[] hash = SHA256.HashData(buffer.AsSpan());
            return Convert.ToBase64String(hash);
        };

        // Cache for per-request instances — this delegate only accesses model properties.
        s_etagCache.TryAdd(GetType(), _getETag);
    }

    private IReadOnlyCollection<string>? _etagPropertyNames;

    /// <summary>
    /// Extracts CLR property names when EVERY selector is a direct member access on the lambda
    /// parameter (after Convert-stripping); returns <c>null</c> as soon as any selector is
    /// computed/nested — the caller treats null as "names unknowable".
    /// </summary>
    private static IReadOnlyCollection<string>? TryExtractDirectMemberNames(
        Expression<Func<TModel, object?>>[] selectors)
    {
        var names = new List<string>(selectors.Length);
        foreach (Expression body in selectors
            .Select(s => s.Body)
            .Select(b => b is UnaryExpression unary &&
                (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked)
                ? unary.Operand
                : b))
        {
            if (body is not MemberExpression member || member.Expression is not ParameterExpression)
                return null;
            names.Add(member.Member.Name);
        }

        return names;
    }

    private bool _authRequired;
    private string? _authPolicy;
    private IReadOnlyList<string>? _authRoles;
    private List<OperationAuthRule>? _operationAuthRules;
    private bool _isAdvancedConfigureOverridden;

    private readonly ICollection<Action<EntityTypeConfiguration<TModel>>> _configurators;
    private readonly ICollection<Delegate> _functions;
    private readonly ICollection<Delegate> _actions;
    private readonly ICollection<Delegate> _entityFunctions;
    private readonly ICollection<Delegate> _entityActions;
    private readonly List<NavigationRouteDefinition> _navRoutes = new();

    /// <summary>
    /// Initialises the profile. Pass a key-selector expression that identifies the entity's
    /// primary key property.
    /// </summary>
    /// <param name="getKey">
    /// Expression that selects the key property from <typeparamref name="TModel"/>,
    /// e.g. <c>x => x.Id</c>.
    /// </param>
    protected EntitySetProfile(Expression<Func<TModel, TKey>> getKey)
    {
        _getKey = getKey;

        var keyBody = getKey.Body is System.Linq.Expressions.UnaryExpression u ? u.Operand : getKey.Body;
        if (keyBody is not System.Linq.Expressions.MemberExpression)
        {
            throw new ArgumentException(
                "The key selector must be a direct property access expression (e.g. x => x.Id). " +
                "Computed or chained key selectors are not supported.",
                nameof(getKey));
        }

        EntitySetName = PluralizationHelper.Pluralize(typeof(TModel).Name);

        _configurators = new List<Action<EntityTypeConfiguration<TModel>>>();
        _functions = new List<Delegate>();
        _actions = new List<Delegate>();
        _entityFunctions = new List<Delegate>();
        _entityActions = new List<Delegate>();
    }

    /// <summary>
    /// Hands over full configuration control. If this method is overridden, you are ejecting
    /// from all other configuration behaviors of this class.
    /// </summary>
    protected virtual void AdvancedConfigure(EntitySetConfiguration<TModel> configuration) { }

    // explicit interface implementation to enforce internal
    void IVisitModelBuilder.VisitModelBuilder(ODataModelBuilder builder, EntitySetDefaults defaults)
    {
        var entitySet = builder.EntitySet<TModel>(EntitySetName);

        _resolvedMaxTop = MaxTop ?? defaults.MaxTop;
        _resolvedMaxRequestBodyBytes = MaxRequestBodyBytes ?? defaults.MaxRequestBodyBytes;
        _resolvedMaxExpansionDepth = MaxExpansionDepth ?? defaults.MaxExpansionDepth;
        _resolvedMaxFilterNodeCount = MaxFilterNodeCount ?? defaults.MaxFilterNodeCount;
        _resolvedMaxOrderByNodeCount = MaxOrderByNodeCount ?? defaults.MaxOrderByNodeCount;
        _resolvedMaxAnyAllExpressionDepth = MaxAnyAllExpressionDepth ?? defaults.MaxAnyAllExpressionDepth;
        _resolvedIdempotentDelete = IdempotentDelete ?? defaults.IdempotentDelete;
        _resolvedAllowUpsert = AllowUpsert ?? defaults.AllowUpsert;
        _resolvedFilterEnabled = FilterEnabled ?? defaults.FilterEnabled;
        _resolvedOrderByEnabled = OrderByEnabled ?? defaults.OrderByEnabled;
        _resolvedSelectEnabled = SelectEnabled ?? defaults.SelectEnabled;
        _resolvedExpandEnabled = ExpandEnabled ?? defaults.ExpandEnabled;
        _resolvedCountEnabled = CountEnabled ?? defaults.CountEnabled;
        _resolvedPropertyAccessEnabled = PropertyAccessEnabled ?? defaults.PropertyAccessEnabled;
        _resolvedSelectPushdownEnabled = SelectPushdownEnabled ?? defaults.SelectPushdownEnabled;
        _resolvedExpandPushdownEnabled = ExpandPushdownEnabled ?? defaults.ExpandPushdownEnabled;
        _resolvedPropertyRouteDocsEnabled = PropertyRouteDocsEnabled ?? defaults.PropertyRouteDocsEnabled;
        _resolvedAllowDeepInsert = AllowDeepInsert ?? defaults.AllowDeepInsert;
        _resolvedRoundingMode = RoundingMode ?? defaults.RoundingMode;
        _structuralProperties = BuildStructuralProperties();

        // #226: a name that is both ignored and declared as a navigation is a config
        // contradiction. Checked here (not in Ignore()) so it is declaration-order-independent.
        foreach (string ignored in _ignoredPropertyNames.Where(_navigationPropertyNames.Contains))
        {
            throw new InvalidOperationException(
                $"Entity set '{EntitySetName}': property '{ignored}' is declared both as a " +
                "navigation property (HasMany/HasOptional/HasRequired) and in Ignore(). " +
                "Remove one of the declarations.");
        }

        AdvancedConfigure(entitySet);

        // eject if AdvancedConfigure was overridden
        var advancedConfigureDeclaredInType = GetType()
            .GetMethod(
                nameof(AdvancedConfigure),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null,
                new[] { typeof(EntitySetConfiguration<TModel>) },
                null)
            ?.DeclaringType;
        _isAdvancedConfigureOverridden = advancedConfigureDeclaredInType != typeof(EntitySetProfile<TKey, TModel>);
        if (_isAdvancedConfigureOverridden) return;

        // if AdvancedConfigure wasn't overridden, work your magic
        var entityType = entitySet.EntityType;

        if (SelectEnabled ?? defaults.SelectEnabled) entityType.Select(_selectProperties);
        // Issue #183: pass an explicit max expansion depth so nested $expand
        // (e.g. $expand=A($expand=B($expand=C))) is not rejected by the model-bound default of 2.
        // The runtime recursion in OhDataEndpointFactory.ExpandLevelAsync bounds actual execution.
        if (ExpandEnabled ?? defaults.ExpandEnabled)
            entityType.Expand(OhData.AspNetCore.OhDataEndpointFactory.MaxNestedExpandDepth, _expandProperties!);
        if (FilterEnabled ?? defaults.FilterEnabled)
            entityType.Filter(MergeAllowlistWithNavigationProperties(_filterProperties));
        if (OrderByEnabled ?? defaults.OrderByEnabled)
            entityType.OrderBy(MergeAllowlistWithNavigationProperties(_orderByProperties));
        if (CountEnabled ?? defaults.CountEnabled) entityType.Count();

        entityType.HasKey(_getKey);
        foreach (var configurator in _configurators) configurator(entityType);

        var entityCollection = entityType.Collection;

        foreach (var method in _functions.Select(x => x.Method))
            RegisterEdmOperation(method, entityCollection.Function(method.Name), typeof(FunctionConfiguration), skipKeyParameter: false);

        foreach (var method in _actions.Select(x => x.Method))
            RegisterEdmOperation(method, entityCollection.Action(method.Name), typeof(ActionConfiguration), skipKeyParameter: false);

        // Gap 7: entity-level functions/actions bind to the entity type, not the collection. Their
        // first parameter is the entity key (TKey) — skipped here, since it is not an OData parameter.
        foreach (var method in _entityFunctions.Select(x => x.Method))
            RegisterEdmOperation(method, entityType.Function(method.Name), typeof(FunctionConfiguration), skipKeyParameter: true);

        foreach (var method in _entityActions.Select(x => x.Method))
            RegisterEdmOperation(method, entityType.Action(method.Name), typeof(ActionConfiguration), skipKeyParameter: true);

        _resolvedBoundFunctions = _functions.Select(d => BoundOperationDefinition.From(d, isAction: false))
            .Concat(_entityFunctions.Select(d => BoundOperationDefinition.From(d, isAction: false, isEntityLevel: true)))
            .ToList();
        _resolvedBoundActions = _actions.Select(d => BoundOperationDefinition.From(d, isAction: true))
            .Concat(_entityActions.Select(d => BoundOperationDefinition.From(d, isAction: true, isEntityLevel: true)))
            .ToList();
        _isSealed = true;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the profile has already been sealed
    /// by the framework. Mutating methods call this guard to enforce constructor-only configuration.
    /// </summary>
    private void ThrowIfSealed()
    {
        if (_isSealed)
        {
            throw new InvalidOperationException(
                "This profile has already been registered and cannot be modified. " +
                "Configure the profile entirely within the constructor.");
        }
    }

    /// <summary>
    /// Unions this entity's navigation property names into a configured
    /// FilterProperties/OrderByProperties allowlist before it is handed to the model builder.
    /// </summary>
    /// <remarks>
    /// FilterProperties/OrderByProperties are documented (and intended) to gate which
    /// STRUCTURAL properties of this entity are usable in <c>$filter</c>/<c>$orderby</c> — not
    /// which navigation properties can be traversed into. Navigation-target types have no
    /// allowlist surface of their own in 1.0 (see
    /// <c>OhDataBuilder.MarkNavigationTargetTypesFullyQueryable</c>), so a path like
    /// <c>Tags/any(t: t/Name eq 'X')</c> should never be affected by this entity's own
    /// allowlist. Microsoft's model-bound validator, however, treats a configured allowlist as
    /// exhaustive for the WHOLE type it's attached to — including navigation properties — so
    /// leaving navigation property names out of the array passed to <c>Filter()</c>/
    /// <c>OrderBy()</c> would make the mere presence of a nav property in the path 400,
    /// regardless of what's on the other side of it. Passing <c>null</c> through unchanged
    /// preserves the "no allowlist configured" case, where <c>Filter()</c>/<c>OrderBy()</c>
    /// with no properties already marks the whole type (all properties, structural and
    /// navigation alike) permissive.
    /// </remarks>
    private string[]? MergeAllowlistWithNavigationProperties(string[]? allowlist)
    {
        if (allowlist is null || _navigationPropertyNames.Count == 0) return allowlist;
        return allowlist.Union(_navigationPropertyNames, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Enumerates the public, readable, non-indexer instance properties of
    /// <typeparamref name="TModel"/>, excluding any property declared as a navigation via
    /// <c>HasOptional</c>, <c>HasRequired</c>, or <c>HasMany</c> (any overload). Runs once at
    /// startup (called from <c>VisitModelBuilder</c>); compiled accessor delegates are cached
    /// statically per property name so this never re-reflects per request.
    /// </summary>
    private List<StructuralPropertyInfo> BuildStructuralProperties()
    {
        string keyPropertyName = GetNavigationPropertyName(_getKey.Body);
        var list = new List<StructuralPropertyInfo>();

        foreach (var prop in typeof(TModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(prop => prop.CanRead)
            .Where(prop => prop.GetIndexParameters().Length == 0) // skip indexers
            .Where(prop => !_navigationPropertyNames.Contains(prop.Name))
            .Where(prop => !_ignoredPropertyNames.Contains(prop.Name))) // #226
        {
            list.Add(new StructuralPropertyInfo
            {
                // #253: the property's OData/EDM name is its [JsonPropertyName] when present, else
                // the CLR name. This is the identifier used for the property route segment, the
                // $select/$filter/$orderby allowlist, and the $select post-strip — so it agrees with
                // the response payload key (which System.Text.Json also derives from [JsonPropertyName]).
                Name = ODataPropertyNaming.ResolveEdmName(prop),
                ClrType = prop.PropertyType,
                IsKey = string.Equals(prop.Name, keyPropertyName, StringComparison.Ordinal),
                IsNullable = IsNullableClrType(prop.PropertyType),
                IsComplex = !IsPrimitiveODataClrType(prop.PropertyType),
                Accessor = CompileStructuralAccessor(prop),
                Property = prop,
            });
        }

        return list;
    }

    private static Func<object, object?> CompileStructuralAccessor(PropertyInfo prop)
    {
        Func<TModel, object?> typed = s_structuralAccessorCache.GetOrAdd(prop.Name, _ =>
        {
            ParameterExpression param = Expression.Parameter(typeof(TModel), "m");
            MemberExpression propAccess = Expression.Property(param, prop);
            UnaryExpression boxed = Expression.Convert(propAccess, typeof(object));
            return Expression.Lambda<Func<TModel, object?>>(boxed, param).Compile();
        });
        return model => typed((TModel)model);
    }

    private static bool IsNullableClrType(Type type) =>
        !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static bool IsPrimitiveODataClrType(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsEnum || s_primitiveClrTypes.Contains(underlying);
    }

    /// <summary>
    /// Restricts the properties that may appear in <c>$filter</c> queries.
    /// Set using either this overload or the string overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void FilterProperties(params Expression<Func<TModel, object?>>[] properties)
    {
        _filterProperties = ExtractNames(properties);
    }

    /// <summary>
    /// Restricts the properties that may appear in <c>$filter</c> queries.
    /// Set using either this overload or the expression overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void FilterProperties(params string[]? properties)
    {
        _filterProperties = properties;
    }

    /// <summary>
    /// Restricts the properties that may appear in <c>$orderby</c> clauses.
    /// Set using either this overload or the string overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void OrderByProperties(params Expression<Func<TModel, object?>>[] properties)
    {
        _orderByProperties = ExtractNames(properties);
    }

    /// <summary>
    /// Restricts the properties that may appear in <c>$orderby</c> clauses.
    /// Set using either this overload or the expression overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void OrderByProperties(params string[]? properties)
    {
        _orderByProperties = properties;
    }

    /// <summary>
    /// Restricts the properties that may appear in <c>$select</c> clauses.
    /// Set using either this overload or the string overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void SelectProperties(params Expression<Func<TModel, object?>>[] properties)
    {
        _selectProperties = ExtractNames(properties);
    }

    /// <summary>
    /// Restricts the properties that may appear in <c>$select</c> clauses.
    /// Set using either this overload or the expression overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void SelectProperties(params string[]? properties)
    {
        _selectProperties = properties;
    }

    /// <summary>
    /// Restricts the properties that may be used in <c>$expand</c> clauses.
    /// Set using either this overload or the string overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void ExpandProperties(params Expression<Func<TModel, object?>>[] properties)
    {
        _expandProperties = ExtractNames(properties);
    }

    /// <summary>
    /// Restricts the properties that may be used in <c>$expand</c> clauses.
    /// Set using either this overload or the expression overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void ExpandProperties(params string[]? properties)
    {
        _expandProperties = properties;
    }

    /// <summary>
    /// Excludes one or more model properties from the entire OData surface (#226): they are
    /// omitted from <c>$metadata</c>, rejected in <c>$select</c>/<c>$filter</c>/<c>$orderby</c>/
    /// <c>$expand</c> (as unknown properties), get no property routes, are omitted from every
    /// response body, and are not bound from POST/PUT/PATCH request bodies. Handlers still see
    /// the full CLR model — only the OData-exposed surface hides ignored properties.
    /// <para>
    /// Multiple calls accumulate. The key property cannot be ignored. A property declared as a
    /// navigation (<c>HasMany</c>/<c>HasOptional</c>/<c>HasRequired</c>) cannot also be ignored —
    /// that combination fails at startup.
    /// </para>
    /// </summary>
    /// <param name="properties">
    /// One or more direct property-access selectors, e.g. <c>x =&gt; x.InternalNotes</c>.
    /// At least one selector is required.
    /// </param>
    protected void Ignore(params Expression<Func<TModel, object?>>[] properties)
    {
        ThrowIfSealed();
        if (properties is null) throw new ArgumentNullException(nameof(properties));
        if (properties.Length == 0)
            throw new ArgumentException("At least one property selector is required.", nameof(properties));

        string keyPropertyName = GetNavigationPropertyName(_getKey.Body);
        for (int i = 0; i < properties.Length; i++)
        {
            MemberExpression member = GetDirectMember(properties[i].Body, i, nameof(properties));

            string name = member.Member.Name;
            if (string.Equals(name, keyPropertyName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The key property '{name}' cannot be ignored — the key is required for " +
                    "routing, entity-id URLs, and $metadata.", nameof(properties));
            }

            if (member.Member is not PropertyInfo clrProperty)
            {
                throw new ArgumentException(
                    $"'{name}' is not a property of {typeof(TModel).Name} — fields cannot be ignored.",
                    nameof(properties));
            }

            if (!_ignoredPropertyNames.Add(name)) continue; // duplicate — already configured

            // EDM removal rides the configurator pipeline, so it is auto-ejected when
            // AdvancedConfigure is overridden (rows 1-2 of the spec's suppression table) while
            // runtime suppression (routes, wire, PATCH) still applies. ModelBuilder's
            // PropertySelectorVisitor rejects the boxing Convert node an
            // Expression<Func<TModel, object?>> carries for value-typed properties
            // ("Unsupported Expression NodeType"), so rebuild an unboxed, strongly-typed
            // selector and invoke Ignore<TProperty> with the property's real CLR type.
            ParameterExpression p = Expression.Parameter(typeof(TModel), "x");
            LambdaExpression typedSelector = Expression.Lambda(
                typeof(Func<,>).MakeGenericType(typeof(TModel), clrProperty.PropertyType),
                Expression.Property(p, clrProperty), p);
            MethodInfo edmIgnore = s_edmIgnoreMethodDefinition.MakeGenericMethod(clrProperty.PropertyType);
            _configurators.Add(cfg => edmIgnore.Invoke(cfg, new object[] { typedSelector }));
        }
    }

    /// <summary>
    /// Extracts member names from a set of simple property-access expressions.
    /// Throws <see cref="ArgumentException"/> if an expression is not a direct member access
    /// on the lambda parameter (#227): x => x.Name.Length or x => x.Category.Name is rejected
    /// rather than silently allowlisting a name that isn't a property of TModel.
    /// </summary>
    private static string[] ExtractNames(Expression<Func<TModel, object?>>[] expressions)
    {
        string[] names = new string[expressions.Length];
        for (int i = 0; i < expressions.Length; i++)
        {
            names[i] = GetDirectMember(expressions[i].Body, i, nameof(expressions)).Member.Name;
        }
        return names;
    }

    /// <summary>
    /// Strips a boxing Convert/ConvertChecked node (e.g. a value type cast to object) and
    /// validates that the remaining body is a member access made directly on the lambda
    /// parameter. Shared by <see cref="ExtractNames"/> and <see cref="Ignore"/> (#226/#227),
    /// which both promise this contract in their error message.
    /// </summary>
    private static MemberExpression GetDirectMember(Expression body, int index, string paramName)
    {
        if (body is UnaryExpression unary &&
            (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression member || member.Expression is not ParameterExpression)
        {
            throw new ArgumentException(
                $"Expression at index {index} must be a direct property access on the model " +
                "(e.g. x => x.Name). Nested access such as x => x.Category.Name is not supported.",
                paramName);
        }

        return member;
    }

    /// <summary>
    /// Declares an optional navigation property in the EDM model without registering a GET route.
    /// </summary>
    /// <typeparam name="TNavigation">The CLR type of the related entity.</typeparam>
    /// <param name="navigation">Expression selecting the navigation property.</param>
    /// <remarks>
    /// #206 phase 2 (Option A1) — <c>$expand</c> pushdown: declaring this navigation <b>without</b>
    /// a delegate opts it <b>into</b> Include pushdown. When an EF Core-backed <c>GetQueryable</c>
    /// source is <c>$expand</c>'d on it, the framework folds the navigation into the collection
    /// query's projection so the related entity loads via a single JOIN'd query (SQL pushdown) —
    /// no delegate needed. Supplying a <c>get</c>/<c>batchGet</c> delegate (the other overloads)
    /// opts the navigation <b>out</b> of pushdown: the delegate then owns expansion (so it can
    /// filter/order/authorize). Mental model: write a delegate only when expansion needs real
    /// logic; a plain relationship gets SQL-JOIN expansion for free. A pushed single-valued
    /// reference honors a nested <c>$select</c> (<c>Ref($select=name)</c>); <c>$filter</c>/
    /// <c>$orderby</c>/<c>$top</c>/<c>$skip</c>/<c>$count</c> do not apply to a single entity.
    /// </remarks>
    protected void HasOptional<TNavigation>(Expression<Func<TModel, TNavigation>> navigation)
        where TNavigation : class
    {
        ThrowIfSealed();
        if (navigation == null) throw new ArgumentNullException(nameof(navigation));
        _navigationPropertyNames.Add(GetNavigationPropertyName(navigation.Body));
        _configurators.Add(x => x.HasOptional(navigation));
    }

    /// <summary>
    /// Declares an optional navigation property and registers a
    /// <c>GET /{EntitySet}({key})/{Property}</c> route backed by <paramref name="get"/>.
    /// Pass <c>null</c> for <paramref name="get"/> to declare the navigation in the EDM only.
    /// Optionally supply <paramref name="refTargetEntitySet"/> to enable populated
    /// <c>@odata.id</c> references on <c>GET /{EntitySet}({key})/{Property}/$ref</c>.
    /// </summary>
    /// <typeparam name="TNavigation">The CLR type of the related entity.</typeparam>
    /// <param name="navigation">Expression selecting the navigation property.</param>
    /// <param name="get">
    /// Handler that loads the related entity by parent key. Return <c>null</c> to produce
    /// a <c>404 Not Found</c> response.
    /// </param>
    /// <param name="refTargetEntitySet">
    /// The entity-set name of the navigation target (e.g. <c>"Suppliers"</c>). When set,
    /// <c>GET /$ref</c> returns an <c>@odata.id</c> link instead of an empty envelope.
    /// The target key is detected by convention: tries <c>Id</c> then <c>{TypeName}Id</c>.
    /// </param>
    protected void HasOptional<TNavigation>(
        Expression<Func<TModel, TNavigation>> navigation,
        Func<TKey, CancellationToken, Task<TNavigation?>>? get,
        string? refTargetEntitySet = null)
        where TNavigation : class
    {
        HasOptional(navigation);
        if (get is null && refTargetEntitySet is null) return;
        string propName = GetNavigationPropertyName(navigation.Body);
        string? childKeyPropName = DetectChildKeyProperty<TNavigation>(refTargetEntitySet);
        _navRoutes.Add(new NavigationRouteDefinition
        {
            PropertyName = propName,
            IsCollection = false,
            Handler = get is not null
                ? async (key, ct) => (object?)await get((TKey)key, ct)
                : (_, _) => Task.FromResult<object?>(null),
            ChildEntitySetName = refTargetEntitySet,
            ChildKeyPropertyName = childKeyPropName,
        });
    }

    /// <summary>
    /// Declares a required navigation property in the EDM model without registering a GET route.
    /// </summary>
    /// <typeparam name="TNavigation">The CLR type of the related entity.</typeparam>
    /// <param name="navigation">Expression selecting the navigation property.</param>
    /// <remarks>
    /// #206 phase 2 (Option A1) — <c>$expand</c> pushdown: declaring this navigation <b>without</b>
    /// a delegate opts it <b>into</b> Include pushdown. When an EF Core-backed <c>GetQueryable</c>
    /// source is <c>$expand</c>'d on it, the framework folds the navigation into the collection
    /// query's projection so the related entity loads via a single JOIN'd query (SQL pushdown) —
    /// no delegate needed. Supplying a <c>get</c>/<c>batchGet</c> delegate (the other overloads)
    /// opts the navigation <b>out</b> of pushdown: the delegate then owns expansion (so it can
    /// filter/order/authorize). Mental model: write a delegate only when expansion needs real
    /// logic; a plain relationship gets SQL-JOIN expansion for free. A pushed single-valued
    /// reference honors a nested <c>$select</c> (<c>Ref($select=name)</c>); <c>$filter</c>/
    /// <c>$orderby</c>/<c>$top</c>/<c>$skip</c>/<c>$count</c> do not apply to a single entity.
    /// </remarks>
    protected void HasRequired<TNavigation>(Expression<Func<TModel, TNavigation>> navigation)
        where TNavigation : class
    {
        ThrowIfSealed();
        if (navigation == null) throw new ArgumentNullException(nameof(navigation));
        _navigationPropertyNames.Add(GetNavigationPropertyName(navigation.Body));
        _configurators.Add(x => x.HasRequired(navigation));
    }

    /// <summary>
    /// Declares a required navigation property and registers a
    /// <c>GET /{EntitySet}({key})/{Property}</c> route backed by <paramref name="get"/>.
    /// Pass <c>null</c> for <paramref name="get"/> to declare the navigation in the EDM only.
    /// Optionally supply <paramref name="refTargetEntitySet"/> to enable populated
    /// <c>@odata.id</c> references on <c>GET /{EntitySet}({key})/{Property}/$ref</c>.
    /// </summary>
    /// <typeparam name="TNavigation">The CLR type of the related entity.</typeparam>
    /// <param name="navigation">Expression selecting the navigation property.</param>
    /// <param name="get">Handler that loads the required related entity by parent key.</param>
    /// <param name="refTargetEntitySet">
    /// The entity-set name of the navigation target (e.g. <c>"Suppliers"</c>). When set,
    /// <c>GET /$ref</c> returns an <c>@odata.id</c> link instead of an empty envelope.
    /// The target key is detected by convention: tries <c>Id</c> then <c>{TypeName}Id</c>.
    /// </param>
    protected void HasRequired<TNavigation>(
        Expression<Func<TModel, TNavigation>> navigation,
        Func<TKey, CancellationToken, Task<TNavigation>>? get,
        string? refTargetEntitySet = null)
        where TNavigation : class
    {
        HasRequired(navigation);
        if (get is null && refTargetEntitySet is null) return;
        string propName = GetNavigationPropertyName(navigation.Body);
        string? childKeyPropName = DetectChildKeyProperty<TNavigation>(refTargetEntitySet);
        _navRoutes.Add(new NavigationRouteDefinition
        {
            PropertyName = propName,
            IsCollection = false,
            Handler = get is not null
                ? async (key, ct) => (object?)await get((TKey)key, ct)
                : (_, _) => Task.FromResult<object?>(null),
            ChildEntitySetName = refTargetEntitySet,
            ChildKeyPropertyName = childKeyPropName,
        });
    }

    /// <summary>
    /// Declares a collection navigation property in the EDM model without registering a GET route.
    /// See the <c>getAll</c>/<c>post</c>/<c>addRef</c>/<c>removeRef</c> overload to also register
    /// GET, POST-create (§11.4.2.1), or <c>$ref</c> routes.
    /// </summary>
    /// <typeparam name="TNavigation">The CLR type of the related entities.</typeparam>
    /// <param name="navigation">Expression selecting the collection navigation property.</param>
    /// <remarks>
    /// #206 phase 2 (Option A1) — <c>$expand</c> pushdown: declaring this navigation <b>without</b>
    /// a delegate opts it <b>into</b> Include pushdown. When an EF Core-backed <c>GetQueryable</c>
    /// source is <c>$expand</c>'d on it, the framework folds the navigation into the collection
    /// query's projection (<c>x =&gt; new TModel { …, Nav = x.Nav.ToList() }</c>) so the related
    /// rows load via a single JOIN'd query (SQL pushdown) — no delegate, no N+1. Supplying a
    /// <c>getAll</c>/<c>batchGetAll</c> delegate (the other overloads) opts the navigation
    /// <b>out</b> of pushdown: the delegate then owns expansion (so it can filter/order/authorize).
    /// Mental model: write a delegate only when expansion needs real logic; a plain relationship
    /// gets SQL-JOIN expansion for free.
    /// <para>
    /// A pushed collection honors the expand's nested options: <c>$filter</c>/<c>$orderby</c>/
    /// <c>$top</c>/<c>$skip</c> push to SQL as a filtered/ordered/paged <c>Include</c> (bound by
    /// Microsoft's own <c>FilterBinder</c>/<c>OrderByBinder</c>), and <c>$count</c>/<c>$select</c>
    /// shape the result. A <b>nested</b> <c>$expand</c> (multi-level) or <c>$levels</c> is NOT
    /// pushed — the navigation then stays EDM-only for that request. See docs/query-options.md.
    /// </para>
    /// </remarks>
    protected void HasMany<TNavigation>(Expression<Func<TModel, IEnumerable<TNavigation>>> navigation)
        where TNavigation : class
    {
        ThrowIfSealed();
        if (navigation == null) throw new ArgumentNullException(nameof(navigation));
        _navigationPropertyNames.Add(GetNavigationPropertyName(navigation.Body));
        _configurators.Add(x => x.HasMany(navigation));
    }

    /// <summary>
    /// Declares a collection navigation property and registers a
    /// <c>GET /{EntitySet}({key})/{Property}</c> route backed by <paramref name="getAll"/>.
    /// Pass <c>null</c> for <paramref name="getAll"/> to declare the navigation in the EDM only.
    /// To also register <c>POST /{EntitySet}({key})/{Property}</c> (create a related entity,
    /// OData §11.4.2.1) or <c>$ref</c> link management, use the overload that accepts
    /// <c>post</c>/<c>addRef</c>/<c>removeRef</c>.
    /// </summary>
    /// <typeparam name="TNavigation">The CLR type of the related entities.</typeparam>
    /// <param name="navigation">Expression selecting the collection navigation property.</param>
    /// <param name="getAll">Handler that loads all related entities for a given parent key.</param>
    protected void HasMany<TNavigation>(
        Expression<Func<TModel, IEnumerable<TNavigation>>> navigation,
        Func<TKey, CancellationToken, Task<IEnumerable<TNavigation>>>? getAll)
        where TNavigation : class
    {
        HasMany(navigation);
        if (getAll is null) return;
        string propName = GetNavigationPropertyName(navigation.Body);
        _navRoutes.Add(new NavigationRouteDefinition
        {
            PropertyName = propName,
            IsCollection = true,
            NavItemType = typeof(TNavigation),
            Handler = async (key, ct) => (object?)await getAll((TKey)key, ct)
        });
    }

    /// <summary>
    /// Declares a collection navigation property with GET, POST-create, and optional <c>$ref</c>
    /// link-management handlers (OData §11.4.2.1 — Create a related entity;
    /// §11.4.6 — Managing Links Between Entities).
    /// </summary>
    /// <typeparam name="TNavigation">The CLR type of the related entities.</typeparam>
    /// <param name="navigation">Expression selecting the collection navigation property.</param>
    /// <param name="getAll">Handler that loads all related entities for a given parent key. Pass <c>null</c> to omit the GET route.</param>
    /// <param name="post">
    /// Handler for <c>POST /{EntitySet}({key})/{Property}</c> (OData §11.4.2.1 — create a new
    /// related entity). Receives the parent key and the deserialized child entity from the
    /// request body; returns the created child (including any server-assigned values, e.g. its
    /// key). Return <c>null</c> to indicate the parent was not found, producing a
    /// <c>404 Not Found</c> response. Pass <c>null</c> (the default) to omit the route.
    /// </param>
    /// <param name="refTargetEntitySet">
    /// When set, the GET <c>$ref</c> handler returns populated <c>@odata.id</c> references, and
    /// the <c>POST</c>-create response's <c>Location</c>/<c>@odata.id</c> can be computed from the
    /// created child's key. The value should be the entity-set name of
    /// <typeparamref name="TNavigation"/> (e.g. <c>"Orders"</c>). The child key property is
    /// detected automatically by convention (<c>Id</c> or <c>{TypeName}Id</c>).
    /// </param>
    /// <param name="addRef">
    /// Handler for <c>POST /{EntitySet}({key})/{Property}/$ref</c>. The second parameter is
    /// the <c>@odata.id</c> string from the request body. Pass <c>null</c> to omit the route.
    /// </param>
    /// <param name="removeRef">
    /// Handler for <c>DELETE /{EntitySet}({key})/{Property}/$ref?$id=...</c>. Pass <c>null</c>
    /// to omit the route.
    /// </param>
    protected void HasMany<TNavigation>(
        Expression<Func<TModel, IEnumerable<TNavigation>>> navigation,
        Func<TKey, CancellationToken, Task<IEnumerable<TNavigation>>>? getAll,
        Func<TKey, TNavigation, CancellationToken, Task<TNavigation?>>? post = null,
        string? refTargetEntitySet = null,
        Func<TKey, string, CancellationToken, Task>? addRef = null,
        Func<TKey, string, CancellationToken, Task>? removeRef = null)
        where TNavigation : class
    {
        HasMany(navigation);
        if (getAll is null && post is null && addRef is null && removeRef is null && refTargetEntitySet is null) return;
        string propName = GetNavigationPropertyName(navigation.Body);

        string? childKeyPropName = DetectChildKeyProperty<TNavigation>(refTargetEntitySet);

        _navRoutes.Add(new NavigationRouteDefinition
        {
            PropertyName = propName,
            IsCollection = true,
            NavItemType = typeof(TNavigation),
            Handler = getAll is not null
                ? async (key, ct) => (object?)await getAll((TKey)key, ct)
                : (_, _) => Task.FromResult<object?>(null),
            PostChild = post is not null
                ? async (key, child, ct) => (object?)await post((TKey)key, (TNavigation)child, ct)
                : (Func<object, object, CancellationToken, Task<object?>>?)null,
            AddRef = addRef is not null
                ? (key, relatedId, ct) => addRef((TKey)key, (string)relatedId, ct)
                : (Func<object, object, CancellationToken, Task>?)null,
            RemoveRef = removeRef is not null
                ? (key, relatedId, ct) => removeRef((TKey)key, (string)relatedId, ct)
                : (Func<object, object, CancellationToken, Task>?)null,
            ChildEntitySetName = refTargetEntitySet,
            ChildKeyPropertyName = childKeyPropName,
        });
    }

    /// <summary>
    /// Declares an optional navigation property with GET and optional <c>$ref</c> link-management
    /// handlers (OData §11.4.6 — Managing Links Between Entities).
    /// </summary>
    /// <typeparam name="TNavigation">The CLR type of the related entity.</typeparam>
    /// <param name="navigation">Expression selecting the navigation property.</param>
    /// <param name="get">Handler that loads the related entity by parent key. Pass <c>null</c> to omit the GET route.</param>
    /// <param name="setRef">
    /// Handler for <c>PUT /{EntitySet}({key})/{Property}/$ref</c>. The second parameter is
    /// the <c>@odata.id</c> string from the request body. Pass <c>null</c> to omit the route.
    /// </param>
    /// <param name="removeRef">
    /// Handler for <c>DELETE /{EntitySet}({key})/{Property}/$ref</c>. Pass <c>null</c>
    /// to omit the route.
    /// </param>
    /// <param name="refTargetEntitySet">
    /// The entity-set name of the navigation target (e.g. <c>"Suppliers"</c>). When set,
    /// <c>GET /$ref</c> returns an <c>@odata.id</c> link instead of an empty envelope.
    /// The target key is detected by convention: tries <c>Id</c> then <c>{TypeName}Id</c>.
    /// </param>
    protected void HasOptional<TNavigation>(
        Expression<Func<TModel, TNavigation>> navigation,
        Func<TKey, CancellationToken, Task<TNavigation?>>? get,
        Func<TKey, string, CancellationToken, Task>? setRef = null,
        Func<TKey, string, CancellationToken, Task>? removeRef = null,
        string? refTargetEntitySet = null)
        where TNavigation : class
    {
        HasOptional(navigation);
        if (get is null && setRef is null && removeRef is null && refTargetEntitySet is null) return;
        string propName = GetNavigationPropertyName(navigation.Body);
        string? childKeyPropName = DetectChildKeyProperty<TNavigation>(refTargetEntitySet);
        _navRoutes.Add(new NavigationRouteDefinition
        {
            PropertyName = propName,
            IsCollection = false,
            Handler = get is not null
                ? async (key, ct) => (object?)await get((TKey)key, ct)
                : (_, _) => Task.FromResult<object?>(null),
            AddRef = setRef is not null
                ? (key, relatedId, ct) => setRef((TKey)key, (string)relatedId, ct)
                : (Func<object, object, CancellationToken, Task>?)null,
            RemoveRef = removeRef is not null
                ? (key, relatedId, ct) => removeRef((TKey)key, (string)relatedId, ct)
                : (Func<object, object, CancellationToken, Task>?)null,
            ChildEntitySetName = refTargetEntitySet,
            ChildKeyPropertyName = childKeyPropName,
        });
    }

    /// <summary>
    /// Declares a collection navigation property and registers a batch-loaded
    /// <c>GET /{EntitySet}({key})/{Property}</c> route. Unlike the per-entity <c>getAll</c>
    /// overload, <paramref name="batchGetAll"/> is invoked once per expanded property per page
    /// during <c>$expand</c> (OData §11.2.4.2) instead of once per parent entity, eliminating
    /// the N+1 query pattern for collection navigations. A per-entity handler is automatically
    /// derived from <paramref name="batchGetAll"/>, so <c>GET /{EntitySet}({key})/{Property}</c>
    /// and <c>/{Property}/$count</c> keep working without any extra registration.
    /// </summary>
    /// <typeparam name="TNavigation">The CLR type of the related entities.</typeparam>
    /// <param name="navigation">Expression selecting the collection navigation property.</param>
    /// <param name="batchGetAll">
    /// Handler that loads all related entities for a set of parent keys in a single call,
    /// grouped by parent key, e.g.
    /// <c>(ids, ct) =&gt; db.Children.Where(c =&gt; ids.Contains(c.ParentId)).ToLookupAsync(c =&gt; c.ParentId, ct)</c>.
    /// </param>
    /// <remarks>
    /// This overload does not accept a <c>post</c> handler, so it never registers
    /// <c>POST /{EntitySet}({key})/{Property}</c> (OData §11.4.2.1). Use the
    /// <c>getAll</c>/<c>post</c>/<c>addRef</c>/<c>removeRef</c> overload when POST-create or
    /// <c>$ref</c> link management is also needed.
    /// </remarks>
    protected void HasMany<TNavigation>(
        Expression<Func<TModel, IEnumerable<TNavigation>>> navigation,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<ILookup<TKey, TNavigation>>> batchGetAll)
        where TNavigation : class
    {
        HasMany(navigation);
        if (batchGetAll == null) throw new ArgumentNullException(nameof(batchGetAll));
        string propName = GetNavigationPropertyName(navigation.Body);

        Func<IReadOnlyList<object>, CancellationToken, Task<IReadOnlyDictionary<object, object?>>> batch =
            async (keys, ct) =>
            {
                var typedKeys = new List<TKey>(keys.Count);
                foreach (object k in keys) typedKeys.Add((TKey)k);
                ILookup<TKey, TNavigation> lookup = await batchGetAll(typedKeys, ct);
                var map = new Dictionary<object, object?>(keys.Count);
                foreach (var group in lookup
                    .Where(group => group.Key is not null)
                    .Select(group => new { Key = (object)group.Key!, Items = group.ToList() }))
                {
                    map[group.Key] = group.Items;
                }
                return map;
            };

        _navRoutes.Add(new NavigationRouteDefinition
        {
            PropertyName = propName,
            IsCollection = true,
            NavItemType = typeof(TNavigation),
            BatchHandler = batch,
            Handler = async (key, ct) =>
            {
                IReadOnlyDictionary<object, object?> map = await batch(new[] { key }, ct);
                return map.TryGetValue(key, out object? v) ? v : Array.Empty<TNavigation>();
            },
        });
    }

    /// <summary>
    /// Declares an optional navigation property and registers a batch-loaded
    /// <c>GET /{EntitySet}({key})/{Property}</c> route. Unlike the per-entity <c>get</c>
    /// overload, <paramref name="batchGet"/> is invoked once per expanded property per page
    /// during <c>$expand</c> (OData §11.2.4.2) instead of once per parent entity, eliminating
    /// the N+1 query pattern. A per-entity handler is automatically derived from
    /// <paramref name="batchGet"/>, so <c>GET /{EntitySet}({key})/{Property}</c> keeps working
    /// without any extra registration.
    /// </summary>
    /// <typeparam name="TNavigation">The CLR type of the related entity.</typeparam>
    /// <param name="navigation">Expression selecting the navigation property.</param>
    /// <param name="batchGet">
    /// Handler that loads the related entity for a set of parent keys in a single call. Parent
    /// keys with no related entity should be absent from the result, or mapped to <c>null</c>.
    /// </param>
    /// <param name="refTargetEntitySet">
    /// The entity-set name of the navigation target (e.g. <c>"Suppliers"</c>). When set,
    /// <c>GET /$ref</c> returns an <c>@odata.id</c> link instead of an empty envelope.
    /// The target key is detected by convention: tries <c>Id</c> then <c>{TypeName}Id</c>.
    /// </param>
    protected void HasOptional<TNavigation>(
        Expression<Func<TModel, TNavigation>> navigation,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TNavigation?>>> batchGet,
        string? refTargetEntitySet = null)
        where TNavigation : class
    {
        HasOptional(navigation);
        if (batchGet == null) throw new ArgumentNullException(nameof(batchGet));
        string propName = GetNavigationPropertyName(navigation.Body);
        string? childKeyPropName = DetectChildKeyProperty<TNavigation>(refTargetEntitySet);

        Func<IReadOnlyList<object>, CancellationToken, Task<IReadOnlyDictionary<object, object?>>> batch =
            async (keys, ct) =>
            {
                var typedKeys = new List<TKey>(keys.Count);
                foreach (object k in keys) typedKeys.Add((TKey)k);
                IReadOnlyDictionary<TKey, TNavigation?> result = await batchGet(typedKeys, ct);
                var map = new Dictionary<object, object?>(keys.Count);
                foreach (var kvp in result
                    .Where(kvp => kvp.Key is not null)
                    .Select(kvp => new { Key = (object)kvp.Key!, kvp.Value }))
                {
                    map[kvp.Key] = kvp.Value;
                }
                return map;
            };

        _navRoutes.Add(new NavigationRouteDefinition
        {
            PropertyName = propName,
            IsCollection = false,
            BatchHandler = batch,
            Handler = async (key, ct) =>
            {
                IReadOnlyDictionary<object, object?> map = await batch(new[] { key }, ct);
                return map.TryGetValue(key, out object? v) ? v : null;
            },
            ChildEntitySetName = refTargetEntitySet,
            ChildKeyPropertyName = childKeyPropName,
        });
    }

    /// <summary>
    /// Declares a required navigation property and registers a batch-loaded
    /// <c>GET /{EntitySet}({key})/{Property}</c> route. Unlike the per-entity <c>get</c>
    /// overload, <paramref name="batchGet"/> is invoked once per expanded property per page
    /// during <c>$expand</c> (OData §11.2.4.2) instead of once per parent entity, eliminating
    /// the N+1 query pattern. A per-entity handler is automatically derived from
    /// <paramref name="batchGet"/>, so <c>GET /{EntitySet}({key})/{Property}</c> keeps working
    /// without any extra registration.
    /// </summary>
    /// <typeparam name="TNavigation">The CLR type of the related entity.</typeparam>
    /// <param name="navigation">Expression selecting the navigation property.</param>
    /// <param name="batchGet">
    /// Handler that loads the required related entity for a set of parent keys in a single
    /// call. Parent keys missing from the result produce a <c>null</c> expanded value.
    /// </param>
    /// <param name="refTargetEntitySet">
    /// The entity-set name of the navigation target (e.g. <c>"Suppliers"</c>). When set,
    /// <c>GET /$ref</c> returns an <c>@odata.id</c> link instead of an empty envelope.
    /// The target key is detected by convention: tries <c>Id</c> then <c>{TypeName}Id</c>.
    /// </param>
    protected void HasRequired<TNavigation>(
        Expression<Func<TModel, TNavigation>> navigation,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TNavigation>>> batchGet,
        string? refTargetEntitySet = null)
        where TNavigation : class
    {
        HasRequired(navigation);
        if (batchGet == null) throw new ArgumentNullException(nameof(batchGet));
        string propName = GetNavigationPropertyName(navigation.Body);
        string? childKeyPropName = DetectChildKeyProperty<TNavigation>(refTargetEntitySet);

        Func<IReadOnlyList<object>, CancellationToken, Task<IReadOnlyDictionary<object, object?>>> batch =
            async (keys, ct) =>
            {
                var typedKeys = new List<TKey>(keys.Count);
                foreach (object k in keys) typedKeys.Add((TKey)k);
                IReadOnlyDictionary<TKey, TNavigation> result = await batchGet(typedKeys, ct);
                var map = new Dictionary<object, object?>(keys.Count);
                foreach (var kvp in result
                    .Where(kvp => kvp.Key is not null)
                    .Select(kvp => new { Key = (object)kvp.Key!, kvp.Value }))
                {
                    map[kvp.Key] = kvp.Value;
                }
                return map;
            };

        _navRoutes.Add(new NavigationRouteDefinition
        {
            PropertyName = propName,
            IsCollection = false,
            BatchHandler = batch,
            Handler = async (key, ct) =>
            {
                IReadOnlyDictionary<object, object?> map = await batch(new[] { key }, ct);
                return map.TryGetValue(key, out object? v) ? v : null;
            },
            ChildEntitySetName = refTargetEntitySet,
            ChildKeyPropertyName = childKeyPropName,
        });
    }

    /// <summary>
    /// Registers a collection-bound OData function: <c>GET /{EntitySet}/{MethodName}?param=value</c>
    /// (OData §11.5.3 — Invoking a Function). Parameters are read from the query string.
    /// The method name becomes the function name in the EDM.
    /// </summary>
    /// <param name="handler">
    /// A delegate whose method name is the function name. Parameters (excluding
    /// <see cref="CancellationToken"/>) are exposed as OData function parameters.
    /// </param>
    protected void BindFunction(Delegate handler)
    {
        ThrowIfSealed();
        _functions.Add(handler ?? throw new ArgumentNullException(nameof(handler)));
    }

    /// <summary>
    /// Registers a collection-bound OData action: <c>POST /{EntitySet}/{MethodName}</c>
    /// (OData §11.5.4 — Invoking an Action). Parameters are read from a JSON request body.
    /// The method name becomes the action name in the EDM.
    /// </summary>
    /// <param name="handler">
    /// A delegate whose method name is the action name. Parameters (excluding
    /// <see cref="CancellationToken"/>) are read from the JSON body as named properties.
    /// </param>
    protected void BindAction(Delegate handler)
    {
        ThrowIfSealed();
        _actions.Add(handler ?? throw new ArgumentNullException(nameof(handler)));
    }

    /// <summary>
    /// Registers an entity-level bound function: <c>GET /{EntitySet}({key})/{MethodName}</c>
    /// (OData §11.5.4 — Functions Bound to an Entity). The handler's first non-<see cref="CancellationToken"/>
    /// parameter must accept the key (<typeparamref name="TKey"/>); additional parameters are
    /// read from the query string.
    /// </summary>
    /// <param name="handler">
    /// A delegate whose method name is the function name. The first parameter receives the
    /// entity key; remaining parameters (excluding <see cref="CancellationToken"/>) are OData
    /// function parameters.
    /// </param>
    protected void BindEntityFunction(Delegate handler)
    {
        ThrowIfSealed();
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        ValidateEntityBoundOperationSignature(handler, nameof(BindEntityFunction));
        _entityFunctions.Add(handler);
    }

    /// <summary>
    /// Registers an entity-level bound action: <c>POST /{EntitySet}({key})/{MethodName}</c>
    /// (OData §11.5.4 — Actions Bound to an Entity). The handler's first non-<see cref="CancellationToken"/>
    /// parameter must accept the key (<typeparamref name="TKey"/>); additional parameters are
    /// read from a JSON request body.
    /// </summary>
    /// <param name="handler">
    /// A delegate whose method name is the action name. The first parameter receives the
    /// entity key; remaining parameters (excluding <see cref="CancellationToken"/>) are read
    /// from the JSON body as named properties.
    /// </param>
    protected void BindEntityAction(Delegate handler)
    {
        ThrowIfSealed();
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        ValidateEntityBoundOperationSignature(handler, nameof(BindEntityAction));
        _entityActions.Add(handler);
    }

    // S6: entity-bound operations are invoked at request time by placing the parsed route key
    // directly into args[0] (see OhDataEndpointFactory) -- the delegate's Parameters array
    // (BoundOperationDefinition.From) strips only a trailing CancellationToken, so the leading
    // key parameter is NOT excluded despite what earlier docs claimed. A zero-parameter handler
    // (or one whose first parameter isn't TKey) previously registered fine and only failed at
    // request time -- IndexOutOfRangeException for zero parameters, or a DynamicInvoke failure
    // for a mismatched first-parameter type. Catch both at bind time instead.
    private void ValidateEntityBoundOperationSignature(Delegate handler, string bindMethodName)
    {
        MethodInfo method = handler.Method;
        ParameterInfo[] allParams = method.GetParameters();
        bool hasTrailingCt = allParams.Length > 0 && allParams[^1].ParameterType == typeof(CancellationToken);
        ParameterInfo[] visibleParams = hasTrailingCt ? allParams[..^1] : allParams;

        if (visibleParams.Length == 0)
        {
            throw new InvalidOperationException(
                $"{bindMethodName}('{method.Name}') on entity set '{EntitySetName}': the handler must " +
                $"accept the entity key as its first parameter, but '{method.Name}' has no parameters " +
                $"(besides an optional trailing CancellationToken). Expected signature: " +
                $"'{method.Name}({typeof(TKey).Name} key, ...)'.");
        }

        if (visibleParams[0].ParameterType != typeof(TKey))
        {
            throw new InvalidOperationException(
                $"{bindMethodName}('{method.Name}') on entity set '{EntitySetName}': the handler's first " +
                $"parameter must be of type '{typeof(TKey).Name}' (the entity key), but " +
                $"'{method.Name}' declares '{visibleParams[0].Name}' as '{visibleParams[0].ParameterType.Name}'. " +
                $"Expected signature: '{method.Name}({typeof(TKey).Name} {visibleParams[0].Name}, ...)'.");
        }
    }

    private static string GetNavigationPropertyName(Expression body)
    {
        if (body is MemberExpression me) return me.Member.Name;
        if (body is UnaryExpression ue) return GetNavigationPropertyName(ue.Operand);
        throw new ArgumentException(
            $"Cannot extract property name from expression type {body.NodeType}. Use a simple property accessor: x => x.PropertyName.");
    }

    /// <summary>
    /// Detects the key property of <typeparamref name="TNavigation"/> by convention when
    /// <paramref name="refTargetEntitySet"/> is supplied. Tries <c>Id</c> first, then
    /// <c>{TypeName}Id</c>. Returns <c>null</c> when <paramref name="refTargetEntitySet"/>
    /// is <c>null</c> or no matching property is found.
    /// </summary>
    private static string? DetectChildKeyProperty<TNavigation>(string? refTargetEntitySet)
    {
        if (refTargetEntitySet is null) return null;
        string typeName = typeof(TNavigation).Name;
        var idProp = typeof(TNavigation).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?? typeof(TNavigation).GetProperty(typeName + "Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return idProp?.Name;
    }

    // Registers a bound function/action in the EDM: declares its (non-CancellationToken) parameters
    // and — via reflection, since the fluent API only exposes generic Returns<T>/ReturnsCollection<T>
    // — its return type, unwrapping Task<T>/ValueTask<T> and treating void/Task/ValueTask as no return.
    // `skipKeyParameter` drops the leading TKey parameter of entity-level operations.
    private static void RegisterEdmOperation(
        MethodInfo method, OperationConfiguration operation, Type configType, bool skipKeyParameter)
    {
        var parameters = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken));
        if (skipKeyParameter) parameters = parameters.Skip(1);

        foreach (var param in parameters)
        {
            var opParam = operation.Parameter(param.ParameterType, param.Name!);
            if (param.IsOptional) opParam.Optional();
            if (param.HasDefaultValue)
            {
                string defaultStr = param.DefaultValue is bool b ? (b ? "true" : "false") : $"{param.DefaultValue}";
                opParam.HasDefaultValue(defaultStr);
            }
        }

        var rawReturn = method.ReturnType;
        Type? returnType = AsyncDispatchHelper.IsVoidAsyncReturn(rawReturn)
            ? null
            : AsyncDispatchHelper.UnwrapAsyncReturn(rawReturn);
        if (returnType is null) return;

        var collectionElement = GetCollectionElementType(returnType);
        string configMethod = collectionElement is not null ? "ReturnsCollection" : "Returns";
        configType.GetMethod(configMethod, Array.Empty<Type>())!
            .MakeGenericMethod(collectionElement ?? returnType)
            .Invoke(operation, null);
    }

    private static Type? GetCollectionElementType(Type type)
    {
        if (type == typeof(string)) return null; // string is IEnumerable<char> but not a "collection" here
        if (type.IsArray) return type.GetElementType();
        foreach (var iface in new[] { type }.Concat(type.GetInterfaces()))
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }
        return null;
    }

    /// <summary>Requires any authenticated user. May be combined with <see cref="RequireRoles"/>.</summary>
    protected void RequireAuthorization()
    {
        ThrowIfSealed();
        ThrowIfOperationAuthConfigured();
        if (_authRequired)
        {
            throw new InvalidOperationException(
                "Authorization has already been configured on this profile. " +
                "RequireAuthorization() is implicit when RequireAuthorization(policy) or RequireRoles() is called.");
        }

        _authRequired = true;
    }

    /// <summary>
    /// Requires a named ASP.NET Core authorization policy.
    /// Register policies via <c>services.AddAuthorization(o => o.AddPolicy(...))</c>.
    /// May be combined with <see cref="RequireRoles"/>.
    /// </summary>
    protected void RequireAuthorization(string policy)
    {
        ThrowIfSealed();
        ThrowIfOperationAuthConfigured();
        if (_authPolicy is not null)
        {
            throw new InvalidOperationException(
                $"A policy ('{_authPolicy}') has already been set on this profile. Call RequireAuthorization(policy) only once.");
        }

        _authPolicy = policy;
        _authRequired = true;
    }

    /// <summary>
    /// Requires the user to be in at least one of the specified roles (OR semantics).
    /// May be combined with <see cref="RequireAuthorization(string)"/>.
    /// For AND semantics, register a named policy via <c>services.AddAuthorizationBuilder().AddPolicy(...)</c>.
    /// </summary>
    protected void RequireRoles(params string[] roles)
    {
        ThrowIfSealed();
        ThrowIfOperationAuthConfigured();
        if (roles.Length == 0)
            throw new ArgumentException("At least one role must be specified.", nameof(roles));
        if (_authRoles is not null)
        {
            throw new InvalidOperationException(
                $"Roles have already been set on this profile. Call RequireRoles only once.");
        }

        _authRoles = Array.AsReadOnly(roles);
        _authRequired = true;
    }

    /// <summary>
    /// Declares per-operation authorization (#199) using a fluent builder whose per-category lambdas
    /// mirror <c>AuthorizationPolicyBuilder</c>. Requirements within a category combine with AND;
    /// later category rules win on overlap. Cannot be combined with the profile-wide
    /// <see cref="RequireAuthorization()"/>/<see cref="RequireRoles"/> methods — choose one model.
    /// </summary>
    /// <example>
    /// <code>
    /// ConfigureAuthorization(auth =&gt; auth
    ///     .Read(r =&gt; r.AllowAnonymous())
    ///     .Writes(w =&gt; w.RequirePolicy("Editors"))
    ///     .Delete(d =&gt; d.RequireRole("Admin")));
    /// </code>
    /// </example>
    protected void ConfigureAuthorization(Action<IAuthorizationRuleBuilder> configure)
    {
        ThrowIfSealed();
        if (configure is null)
            throw new ArgumentNullException(nameof(configure));
        if (_operationAuthRules is not null)
        {
            throw new InvalidOperationException(
                "ConfigureAuthorization has already been called on this profile. Call it only once.");
        }
        if (_authRequired)
        {
            throw new InvalidOperationException(
                "ConfigureAuthorization cannot be combined with RequireAuthorization()/RequireRoles(). " +
                "Use one authorization model.");
        }

        var builder = new AuthorizationRuleBuilder();
        configure(builder);
        _operationAuthRules = builder.Rules.ToList();
    }

    private void ThrowIfOperationAuthConfigured()
    {
        if (_operationAuthRules is not null)
        {
            throw new InvalidOperationException(
                "RequireAuthorization()/RequireRoles() cannot be combined with ConfigureAuthorization(). " +
                "Use one authorization model.");
        }
    }

    // ── IEntitySetEndpointSource ─────────────────────────────────────────────

    string IEntitySetEndpointSource.EntitySetName => EntitySetName;
    Type IEntitySetEndpointSource.KeyType => typeof(TKey);
    Type IEntitySetEndpointSource.ModelType => typeof(TModel);

    bool IEntitySetEndpointSource.HasGetAll => GetAll is not null;
    bool IEntitySetEndpointSource.HasGetQueryable => GetQueryable is not null;
    bool IEntitySetEndpointSource.HasGetById => GetById is not null;
    bool IEntitySetEndpointSource.HasPost => Post is not null;
    bool IEntitySetEndpointSource.HasPut => Put is not null;
    bool IEntitySetEndpointSource.HasPatch => Patch is not null;
    bool IEntitySetEndpointSource.HasDelete => Delete is not null;
    bool IEntitySetEndpointSource.HasETag => _getETag is not null;
    AuthorizationConfig? IEntitySetEndpointSource.Authorization =>
        _authRequired ? new AuthorizationConfig(true, _authPolicy, _authRoles) : null;
    IReadOnlyList<OperationAuthRule>? IEntitySetEndpointSource.OperationAuthorization => _operationAuthRules;
    IReadOnlyList<NavigationRouteDefinition> IEntitySetEndpointSource.NavigationRoutes => _navRoutes;
    IReadOnlyList<BoundOperationDefinition> IEntitySetEndpointSource.BoundFunctions =>
        _resolvedBoundFunctions ??= _functions.Select(d => BoundOperationDefinition.From(d, isAction: false))
            .Concat(_entityFunctions.Select(d => BoundOperationDefinition.From(d, isAction: false, isEntityLevel: true)))
            .ToList();
    IReadOnlyList<BoundOperationDefinition> IEntitySetEndpointSource.BoundActions =>
        _resolvedBoundActions ??= _actions.Select(d => BoundOperationDefinition.From(d, isAction: true))
            .Concat(_entityActions.Select(d => BoundOperationDefinition.From(d, isAction: true, isEntityLevel: true)))
            .ToList();
    string IEntitySetEndpointSource.InvokeGetETag(object model) => _getETag!((TModel)model);

    private Func<TModel, string>? _keyToString;
    private Func<TModel, string> CompileKeyToString()
    {
        return s_keyToStringCache.GetOrAdd(GetType(), _ =>
        {
            var compiled = _getKey.Compile();
            return model => string.Format(CultureInfo.InvariantCulture, "{0}", compiled(model)) ?? "";
        });
    }
    string IEntitySetEndpointSource.InvokeGetKeyString(object model)
        => LazyInitializer.EnsureInitialized(ref _keyToString, CompileKeyToString)((TModel)model);

    // S4 fix: a second compiled accessor, distinct from CompileKeyToString above. That one
    // formats the key raw/unquoted (used only for PUT's body-vs-URL key equality check, which
    // must compare against the equally-unquoted ODataKeyParser.Parse result). This one formats
    // the key as a canonical, URL-safe OData key literal (quoted + percent-encoded for string
    // keys) for embedding in entity-id URLs (POST 201 Location, OData-EntityId, @odata.id).
    private static readonly ConcurrentDictionary<Type, Func<TModel, string>> s_keyToUrlCache = new();
    private Func<TModel, string>? _keyToUrl;
    private Func<TModel, string> CompileKeyToUrl()
    {
        return s_keyToUrlCache.GetOrAdd(GetType(), _ =>
        {
            var compiled = _getKey.Compile();
            return model => OhData.AspNetCore.ODataEntityKeyUrlFormatter.Format(compiled(model)!);
        });
    }
    string IEntitySetEndpointSource.InvokeGetKeyForUrl(object model)
        => LazyInitializer.EnsureInitialized(ref _keyToUrl, CompileKeyToUrl)((TModel)model);

    int? IEntitySetEndpointSource.MaxTop => _resolvedMaxTop;
    long? IEntitySetEndpointSource.MaxRequestBodyBytes => _resolvedMaxRequestBodyBytes;
    int IEntitySetEndpointSource.MaxExpansionDepth => _resolvedMaxExpansionDepth;
    int IEntitySetEndpointSource.MaxFilterNodeCount => _resolvedMaxFilterNodeCount;
    int IEntitySetEndpointSource.MaxOrderByNodeCount => _resolvedMaxOrderByNodeCount;
    int IEntitySetEndpointSource.MaxAnyAllExpressionDepth => _resolvedMaxAnyAllExpressionDepth;
    bool IEntitySetEndpointSource.IdempotentDelete => _resolvedIdempotentDelete;
    bool IEntitySetEndpointSource.AllowUpsert => _resolvedAllowUpsert;
    bool IEntitySetEndpointSource.HasSearch => Search is not null;
    bool IEntitySetEndpointSource.FilterEnabled => _resolvedFilterEnabled;
    bool IEntitySetEndpointSource.OrderByEnabled => _resolvedOrderByEnabled;
    bool IEntitySetEndpointSource.SelectEnabled => _resolvedSelectEnabled;
    bool IEntitySetEndpointSource.ExpandEnabled => _resolvedExpandEnabled;
    bool IEntitySetEndpointSource.CountEnabled => _resolvedCountEnabled;
    bool IEntitySetEndpointSource.PropertyAccessEnabled => _resolvedPropertyAccessEnabled;
    bool IEntitySetEndpointSource.PropertyRouteDocsEnabled => _resolvedPropertyRouteDocsEnabled;
    bool IEntitySetEndpointSource.SelectPushdownEnabled => _resolvedSelectPushdownEnabled;
    bool IEntitySetEndpointSource.ExpandPushdownEnabled => _resolvedExpandPushdownEnabled;
    IReadOnlyCollection<string>? IEntitySetEndpointSource.ETagPropertyNames => _etagPropertyNames;
    RoundingMode IEntitySetEndpointSource.RoundingMode => _resolvedRoundingMode;
    IReadOnlyList<StructuralPropertyInfo> IEntitySetEndpointSource.StructuralProperties =>
        _structuralProperties ??= BuildStructuralProperties();
    bool IEntitySetEndpointSource.AllowDeepInsert => _resolvedAllowDeepInsert;
    IReadOnlyCollection<string> IEntitySetEndpointSource.NavigationPropertyNames => _navigationPropertyNames;
    IReadOnlyCollection<string> IEntitySetEndpointSource.IgnoredPropertyNames => _ignoredPropertyNames;
    string IEntitySetEndpointSource.KeyPropertyName => GetNavigationPropertyName(_getKey.Body);
    bool IEntitySetEndpointSource.IsAdvancedConfigureOverridden => _isAdvancedConfigureOverridden;

    async Task<IEnumerable<object>> IEntitySetEndpointSource.InvokeSearchAsync(string searchTerm, CancellationToken ct)
    {
        var result = await Search!.Invoke(searchTerm, ct);
        return result.Cast<object>();
    }

    async Task<object?> IEntitySetEndpointSource.InvokeGetAllAsync(CancellationToken ct) =>
        (object?)await GetAll!.Invoke(ct);

    async Task<IQueryable<object>> IEntitySetEndpointSource.InvokeGetQueryableAsync(CancellationToken ct) =>
        (await GetQueryable!.Invoke(ct)).Cast<object>();

    async Task<object?> IEntitySetEndpointSource.InvokeGetByIdAsync(object key, CancellationToken ct) =>
        (object?)await GetById!.Invoke((TKey)key, ct);

    async Task<object?> IEntitySetEndpointSource.InvokePostAsync(object model, CancellationToken ct) =>
        (object?)await Post!.Invoke((TModel)model, ct);

    async Task<object?> IEntitySetEndpointSource.InvokePutAsync(object key, object model, CancellationToken ct) =>
        (object?)await Put!.Invoke((TKey)key, (TModel)model, ct);

    async Task<object?> IEntitySetEndpointSource.InvokePatchAsync(object key, Delta delta, CancellationToken ct) =>
        (object?)await Patch!.Invoke((TKey)key, (Delta<TModel>)delta, ct);

    Task<bool> IEntitySetEndpointSource.InvokeDeleteAsync(object key, CancellationToken ct) =>
        Delete!.Invoke((TKey)key, ct);
}

/// <summary>
/// Simple English pluralisation rules used to derive the default entity set name
/// from the model type name.
/// </summary>
internal static class PluralizationHelper
{
    /// <summary>
    /// Applies simple English pluralisation rules to <paramref name="name"/>:
    /// consonant + y ending replaces y with ies; s/sh/ch/x/z endings append es;
    /// everything else appends s.
    /// </summary>
    internal static string Pluralize(string name)
    {
        if (name.Length == 0) return name;

        // ends in consonant + y  ->  replace y with ies  (Category -> Categories)
        if (name.EndsWith('y') && name.Length > 1 && !"aeiouAEIOU".Contains(name[^2]))
            return name[..^1] + "ies";

        // ends in s, sh, ch, x, z  ->  append es  (Status -> Statuses)
        if (name.EndsWith("sh", StringComparison.Ordinal) ||
            name.EndsWith("ch", StringComparison.Ordinal) ||
            name.EndsWith('s') || name.EndsWith('x') || name.EndsWith('z'))
        {
            return name + "es";
        }

        // default: append s  (Product -> Products, Order -> Orders)
        return name + "s";
    }
}
