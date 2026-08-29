using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using Microsoft.OData.Edm.Vocabularies;
using Microsoft.OData.Edm.Vocabularies.V1;
using Microsoft.OData.ModelBuilder;

namespace OhData;

/// <summary>
/// Fluent builder used inside <c>AddOhData(ohdata => { ... })</c> to register entity set profiles
/// and configure framework options.
/// </summary>
public sealed class OhDataBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<Type> _profileTypes = new();
    private readonly List<UnboundOperationDefinition> _unboundOps = new();
    private string _prefix = "/odata";
    private readonly string _name;
    private readonly EntitySetDefaults _defaults = new();
    // #252: OhData owns its response casing independently of the host's HttpJsonOptions.
    // null = PascalCase (the CLR names $metadata declares, OData §4.4). This is the source of
    // truth for every OhData response path; the host's PropertyNamingPolicy is not inherited.
    private JsonNamingPolicy? _jsonPropertyNamingPolicy;
    // #389: OData open complex types. ON by default -- a complex type with a dictionary member IS an
    // open type, the CSDL this same builder emits has always said OpenType="true" for it, and the
    // developer should not have to know the spec to get the conformant wire shape. WithOpenTypes(false)
    // is the escape hatch back to the pre-#389 nested shape. See WithOpenTypes.
    private bool _openTypesEnabled = true;

    // Tracks profile types registered across all OhData registrations on this IServiceCollection.
    // Stored as a singleton marker so it is shared between all OhDataBuilder instances.
    private sealed class GlobalProfileRegistry
    {
        internal readonly HashSet<Type> RegisteredTypes = new();
    }

    internal OhDataBuilder(IServiceCollection services, string name = OhDataDefaults.DefaultRegistrationName)
    {
        _services = services;
        _name = name;

        // Ensure the cross-registration tracker is registered in DI exactly once,
        // as an instance singleton so it is available before the container is built.
        if (!_services.Any(s => s.ServiceType == typeof(GlobalProfileRegistry)))
            _services.AddSingleton(new GlobalProfileRegistry());

        // Delta-mapping infrastructure. The registry accumulates DeltaProfile types across every
        // OhData registration (instance singleton, mutable before the container is built); the
        // single IDeltaFactory reads it once, lazily, and compiles/validates every mapping (forced
        // at MapOhData for startup fail-fast). Both registered idempotently so multiple AddOhData
        // calls are no-ops here.
        if (!_services.Any(s => s.ServiceType == typeof(DeltaProfileRegistry)))
            _services.AddSingleton(new DeltaProfileRegistry());
        if (!_services.Any(s => s.ServiceType == typeof(IDeltaFactory)))
        {
            _services.AddSingleton<IDeltaFactory>(sp =>
                DeltaFactory.Build(sp, sp.GetRequiredService<DeltaProfileRegistry>()));
        }
    }

    /// <summary>
    /// Sets the route prefix for all OData endpoints. Defaults to <c>/odata</c>.
    /// </summary>
    public OhDataBuilder WithPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Prefix must not be empty.", nameof(prefix));
        string normalized = '/' + prefix.Trim().Trim('/');
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^/[A-Za-z0-9._~!$&'()*+,;=:@/\-]*$"))
        {
            throw new ArgumentException(
                $"Prefix '{normalized}' contains characters that are not valid in a URL path segment.", nameof(prefix));
        }

        _prefix = normalized;
        return this;
    }

    /// <summary>
    /// Configures global defaults applied to all entity sets in this registration
    /// (e.g. <c>MaxTop</c>, <c>IdempotentDelete</c>). Per-profile settings override these.
    /// </summary>
    public OhDataBuilder WithDefaults(Action<EntitySetDefaults> configure)
    {
        configure(_defaults);
        return this;
    }

    /// <summary>
    /// Sets the JSON property-naming policy applied to every OData response payload in this
    /// registration (collection/GetById reads, POST/PUT/PATCH echoes, <c>$select</c>/<c>$expand</c>
    /// output, <c>$value</c>, and function/action results).
    /// </summary>
    /// <remarks>
    /// Defaults to <c>null</c> — <b>PascalCase</b>, the CLR property names OhData's <c>$metadata</c>
    /// declares — so payload casing matches the EDM (OData §4.4). OhData owns this default
    /// independently of the host's <c>HttpJsonOptions.SerializerOptions.PropertyNamingPolicy</c>,
    /// which is intentionally not inherited. Pass <see cref="JsonNamingPolicy.CamelCase"/> to emit
    /// camelCase payloads instead; an explicit value here always wins.
    /// <para>
    /// <b>Warning — this policy shapes payloads and OpenAPI schemas, NOT <c>$metadata</c>.</b> Opting
    /// into camelCase makes response bodies and the generated OpenAPI schema camelCase, but
    /// <c>$metadata</c> continues to advertise the EDM property names — PascalCase, or the
    /// <c>[System.Text.Json.Serialization.JsonPropertyName]</c> value where one is present (§4.4).
    /// The <c>$select</c>/<c>$filter</c>/<c>$orderby</c> parser resolves those EDM names
    /// case-insensitively, so a camelCase query option still binds; but a strict OData-native client
    /// that binds by the exact <c>$metadata</c> name will not match a camelCase payload key under this
    /// opt-in. A <c>[JsonPropertyName]</c> rename is the ONE name used everywhere (metadata, payload,
    /// query options, schema) regardless of this policy — the policy never re-cases a rename.
    /// </para>
    /// </remarks>
    /// <param name="policy">
    /// The naming policy, or <c>null</c> for PascalCase (the default). For example
    /// <see cref="JsonNamingPolicy.CamelCase"/>.
    /// </param>
    public OhDataBuilder WithJsonPropertyNamingPolicy(JsonNamingPolicy? policy)
    {
        _jsonPropertyNamingPolicy = policy;
        return this;
    }

    /// <summary>
    /// Controls OData <b>open complex types</b> (#389) for this registration: a complex type with an
    /// <c>IDictionary&lt;string, object?&gt;</c> member serializes and binds its entries <b>flat</b>
    /// — dynamic keys as siblings of the declared properties, never nested under the member's own
    /// name. <b>On by default</b>; call <c>WithOpenTypes(false)</c> to restore the pre-#389 nested
    /// shape.
    /// </summary>
    /// <remarks>
    /// <b>Why on by default.</b> A complex type with a dictionary member <i>is</i> an open type, and
    /// the CSDL this same builder emits has always said so — <c>ODataConventionModelBuilder</c> marks
    /// the type <c>OpenType="true"</c> and omits the member from the declared properties whether or
    /// not this is enabled. Leaving the wire shape nested made <c>$metadata</c> and the payload
    /// disagree, and made the conformant behaviour something the developer had to know the spec to
    /// ask for. It also put OhData at odds with <c>Microsoft.AspNetCore.OData</c>, whose
    /// <c>ODataResourceSerializer</c> reads the very same <c>DynamicPropertyDictionaryAnnotation</c>
    /// and appends dynamic properties flat with no opt-in flag anywhere in that path. OhData now
    /// auto-maps per the spec; the developer writes a declarative profile.
    /// <para>
    /// <b><c>WithOpenTypes(false)</c> is an escape hatch, and the hazard it exists for is real.</b>
    /// Once the container is <c>System.Text.Json</c> extension data it is no longer a <i>declared</i>
    /// property, so an existing adopter's body <c>{"Meta":{"Bag":{"a":1}}}</c> stops binding
    /// <c>{"a":1}</c> to the <c>Bag</c> property and starts binding a dynamic key literally named
    /// <c>Bag</c> — the handler persists <c>Bag = { "Bag": {"a":1} }</c>. The response echo of that
    /// mis-bound value is <b>byte-identical</b> to the correct one, so an adopter cannot detect the
    /// difference by diffing responses in staging. <c>MapOhData()</c> therefore logs one warning per
    /// affected complex type at startup, naming the CLR type and the container member.
    /// </para>
    /// <para>
    /// <b>Does this affect you at all?</b> Ask one question: <i>do any of your complex types have an
    /// <c>IDictionary&lt;string, object&gt;</c> member?</i> If none do, this setting is inert in
    /// either position — the registration's serializer options are not even derived, no write-side
    /// walk runs, nothing is logged, and every response (error responses included) is byte-identical
    /// to a pre-#389 build.
    /// </para>
    /// <para>
    /// When active this also validates incoming dynamic-property names: a key that is not an OData
    /// simple identifier (CSDL §4.1 <c>odataIdentifier</c> — so the empty string, or anything
    /// containing <c>@</c>, <c>.</c>, whitespace or <c>-</c>) is rejected with <c>400</c>. This runs
    /// on every route that binds a body which can reach a dynamic bag: <c>POST</c>, <c>PUT</c> and
    /// <c>PATCH</c> on the entity, the structural-property write routes, the navigation-<c>POST</c>
    /// create route, and each parameter of a bound or unbound <b>action</b>. It is applied at every
    /// depth — the value of an accepted dynamic key is itself walked, including through arrays and
    /// through dictionary-valued declared members, because everything below a bag key is stored
    /// verbatim and echoed on every later read. (A dictionary member's own map keys are keys of a
    /// <i>declared</i> property, not dynamic property names, so they are not validated — only its
    /// values are walked.)
    /// See <c>docs/open-types.md</c> for the full contract, including <c>PATCH</c>'s whole-value
    /// replace semantics.
    /// </para>
    /// </remarks>
    /// <param name="enabled">
    /// <c>true</c> (the default) for the flat, spec-conformant shape; <c>false</c> to restore the
    /// pre-#389 shape in which the container is an ordinary nested declared property. The name keeps
    /// the <c>With…</c> form the rest of this builder uses (<c>WithPrefix</c>, <c>WithDefaults</c>,
    /// <c>WithJsonPropertyNamingPolicy</c>) rather than gaining a <c>WithoutOpenTypes()</c> sibling,
    /// so there stays exactly one way to set it.
    /// </param>
    public OhDataBuilder WithOpenTypes(bool enabled = true)
    {
        _openTypesEnabled = enabled;
        return this;
    }

    /// <summary>
    /// Registers an entity set profile. The profile is resolved from DI (scoped),
    /// so constructor injection of scoped services (e.g. DbContext) is supported.
    /// </summary>
    public OhDataBuilder AddEntitySetProfile<TProfile>() where TProfile : class, IEntitySetProfile
    {
        RegisterProfileType(typeof(TProfile), explicitCall: true);
        return this;
    }

    // #424: the single guard behind both AddEntitySetProfile<T>() (explicitCall: true) and the
    // AddProfilesFrom* scanner path (explicitCall: false, via AddProfileType below). Before this
    // fix only the explicit path consulted GlobalProfileRegistry, so scanning the same assembly
    // into two named registrations silently allowed exactly what AddEntitySetProfile<T>() rejects
    // -- a profile type belongs to exactly one registration (it is resolved per-registration and
    // its EDM contribution is registration-scoped), and bypassing the guard didn't make sharing
    // work, it just made the failure silent and deferred. One method, not a second parallel check
    // that could drift from this one.
    private void RegisterProfileType(Type type, bool explicitCall)
    {
        if (_profileTypes.Contains(type))
        {
            if (explicitCall)
            {
                throw new InvalidOperationException(
                    $"OhData: profile type '{type.Name}' is already registered. Remove the duplicate AddEntitySetProfile call.");
            }
            return; // scanner re-discovery within THIS registration -- already tracked, no-op
        }

        // Detect the same profile type being registered in a different OhData registration.
        // Retrieve the tracker directly from the registered descriptors to avoid requiring a built container.
        var registryDescriptor = _services.FirstOrDefault(s => s.ServiceType == typeof(GlobalProfileRegistry));
        if (registryDescriptor?.ImplementationInstance is GlobalProfileRegistry registry)
        {
            if (!registry.RegisteredTypes.Add(type))
            {
                throw new InvalidOperationException(
                    $"Profile type '{type.Name}' has already been registered in a different " +
                    "OhData registration. A profile type cannot be shared across registrations.");
            }
        }

        _services.AddScoped(type);
        _profileTypes.Add(type);
    }

    /// <summary>
    /// Registers a <see cref="DeltaProfile"/>. Its mappings are compiled and validated once at
    /// startup and exposed through the injected <see cref="IDeltaFactory"/>. The symmetric
    /// counterpart to <see cref="AddEntitySetProfile{TProfile}"/>.
    /// </summary>
    public OhDataBuilder AddDeltaProfile<TProfile>() where TProfile : DeltaProfile
    {
        AddDeltaProfileType(typeof(TProfile), explicitCall: true);
        return this;
    }

    // Routes a DeltaProfile type into the shared cross-registration registry and DI. Delta
    // profiles are not tied to a single OhData registration (the IDeltaFactory is one global
    // singleton), so uniqueness is tracked in the shared DeltaProfileRegistry rather than the
    // per-builder _profileTypes list.
    private void AddDeltaProfileType(Type type, bool explicitCall)
    {
        var registryDescriptor = _services.FirstOrDefault(s => s.ServiceType == typeof(DeltaProfileRegistry));
        var registry = (DeltaProfileRegistry)registryDescriptor!.ImplementationInstance!;
        if (registry.Types.Contains(type))
        {
            if (explicitCall)
            {
                throw new InvalidOperationException(
                    $"OhData: delta profile type '{type.Name}' is already registered. " +
                    "Remove the duplicate AddDeltaProfile call.");
            }
            return; // scan re-discovery of an already-registered type — skip idempotently
        }

        if (!_services.Any(s => s.ServiceType == type))
            _services.AddScoped(type);
        registry.Types.Add(type);
    }

    /// <summary>
    /// Scans the specified assemblies for <see cref="EntitySetProfile{TKey,TModel}"/> subclasses
    /// and <see cref="DeltaProfile"/> subclasses, registering each discovered profile as if it had
    /// been passed to <see cref="AddEntitySetProfile{TProfile}"/> or
    /// <see cref="AddDeltaProfile{TProfile}"/> individually.
    /// </summary>
    /// <param name="configure">
    /// Callback that receives a <see cref="ProfileScanner"/> and specifies which assemblies
    /// to scan, e.g. <c>s =&gt; s.InAssemblyOf&lt;Program&gt;()</c>.
    /// </param>
    /// <example>
    /// <code>
    /// services.AddOhData(builder =&gt; builder
    ///     .AddProfilesFrom(s =&gt; s
    ///         .InAssemblyOf&lt;Program&gt;()
    ///         .In(typeof(ExternalProfile).Assembly)));
    /// </code>
    /// </example>
    public OhDataBuilder AddProfilesFrom(Action<ProfileScanner> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        var scanner = new ProfileScanner(_profileTypes);
        configure(scanner);
        foreach (var type in scanner.Scan())
        {
            if (typeof(DeltaProfile).IsAssignableFrom(type))
                AddDeltaProfileType(type, explicitCall: false);
            else
                AddProfileType(type);
        }
        return this;
    }

    /// <summary>
    /// Scans the assembly that contains <typeparamref name="T"/> for
    /// <see cref="EntitySetProfile{TKey,TModel}"/> subclasses and registers each one.
    /// Equivalent to <c>AddProfilesFrom(s =&gt; s.InAssemblyOf&lt;T&gt;())</c>.
    /// </summary>
    /// <typeparam name="T">Any type whose containing assembly should be scanned.</typeparam>
    public OhDataBuilder AddProfilesFromAssemblyOf<T>() =>
        AddProfilesFrom(s => s.InAssemblyOf<T>());

    /// <summary>
    /// Scans the specified assemblies for <see cref="EntitySetProfile{TKey,TModel}"/> subclasses
    /// and registers each one.
    /// Equivalent to <c>AddProfilesFrom(s =&gt; s.In(assemblies))</c>.
    /// </summary>
    /// <param name="assemblies">One or more assemblies to scan.</param>
    public OhDataBuilder AddProfilesFromAssembly(params Assembly[] assemblies) =>
        AddProfilesFrom(s => s.In(assemblies));

    // #424: routes through the SAME guard AddEntitySetProfile<T>() uses (explicitCall: false, so a
    // type this registration already tracks is a silent no-op -- ordinary scan re-discovery -- but
    // a type another registration already claimed still throws).
    private void AddProfileType(Type type) => RegisterProfileType(type, explicitCall: false);

    /// <summary>
    /// Registers an unbound function at the service root: <c>GET /prefix/{name}</c>.
    /// Parameters are read from query-string values; <see cref="System.Threading.CancellationToken"/>
    /// is detected and injected automatically if present.
    /// </summary>
    /// <param name="handler">The function delegate. The method name is used as the route segment unless <paramref name="name"/> is specified.</param>
    /// <param name="name">
    /// Optional explicit route name, also the operation's name in <c>$metadata</c>. Required when
    /// the handler is an anonymous lambda or a local function: the compiler-generated method name
    /// is not a valid OData identifier, and passing one is now enforced rather than silently
    /// producing invalid CSDL (#468).
    /// </param>
    /// <exception cref="ArgumentException">
    /// The resolved name is not a valid OData identifier (CSDL 4.0 section 4.1,
    /// <c>odataIdentifier</c>).
    /// </exception>
    public OhDataBuilder AddFunction(Delegate handler, string? name = null)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        var op = UnboundOperationDefinition.From(handler, isAction: false);
        if (name is not null) op = op with { Name = name };
        ValidateUnboundOperationName(
            op.Name, isAction: false, explicitName: name is not null,
            paramName: name is not null ? nameof(name) : nameof(handler));
        // #498: same three signature rules as the profile's Bind* methods, through the same
        // validator. Checked here, at registration, for the reason #468's name check gives above:
        // it fires at the call site that caused it, with the developer's own code on the stack.
        OperationSignatureValidation.Validate(
            handler.Method, op.Name, isAction: false, $"AddFunction('{op.Name}')", nameof(AddAction));
        _unboundOps.Add(op);
        return this;
    }

    /// <summary>
    /// Registers an unbound action at the service root: <c>POST /prefix/{name}</c>.
    /// Parameters are read from the JSON request body; <see cref="System.Threading.CancellationToken"/>
    /// is detected and injected automatically if present.
    /// </summary>
    /// <param name="handler">The action delegate. The method name is used as the route segment unless <paramref name="name"/> is specified.</param>
    /// <param name="name">
    /// Optional explicit route name, also the operation's name in <c>$metadata</c>. Required when
    /// the handler is an anonymous lambda or a local function: the compiler-generated method name
    /// is not a valid OData identifier, and passing one is now enforced rather than silently
    /// producing invalid CSDL (#468).
    /// </param>
    /// <exception cref="ArgumentException">
    /// The resolved name is not a valid OData identifier (CSDL 4.0 section 4.1,
    /// <c>odataIdentifier</c>).
    /// </exception>
    public OhDataBuilder AddAction(Delegate handler, string? name = null)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        var op = UnboundOperationDefinition.From(handler, isAction: true);
        if (name is not null) op = op with { Name = name };
        ValidateUnboundOperationName(
            op.Name, isAction: true, explicitName: name is not null,
            paramName: name is not null ? nameof(name) : nameof(handler));
        // #498: see AddFunction. A void return is legal here — an action with no return value
        // produces 204 No Content — so only the IResult and CancellationToken rules can fire.
        OperationSignatureValidation.Validate(
            handler.Method, op.Name, isAction: true, $"AddAction('{op.Name}')", nameof(AddAction));
        _unboundOps.Add(op);
        return this;
    }

    // #468: an unbound operation's name is written into $metadata as the FunctionImport/
    // ActionImport name AND used verbatim as the route segment, so it must be an OData simple
    // identifier. Nothing checked it. The reachable failure is an anonymous lambda or local
    // function: UnboundOperationDefinition.From falls back to method.Name, and the compiler emits
    // an unspeakable name ("<Caller>b__2_1") that is not a valid identifier -- which shipped as
    // invalid CSDL, because CsdlWriter does not police names and CsdlReader.TryParse accepts them,
    // so only a consumer that VALIDATES the document ever noticed.
    //
    // Checked here, at registration, rather than only at MapOhData(): this fires at the call site
    // that caused it with the developer's own code on the stack, and it can name the remedy --
    // which is sitting in the signature as the optional 'name' parameter. EdmValidator.Validate in
    // MapAll stays as the backstop for constructs this cannot see.
    //
    // The grammar comes from OpenTypeJsonOptions.IsValidDynamicPropertyName -- the SHIPPED
    // validator, deliberately not a second transcription of the ABNF. It dispatches the ASCII fast
    // path or the rune walk that is its normative oracle (see the remarks there and CLAUDE.md);
    // the *Cached variant beside it is not used, since its 1024-entry cache exists to memoise
    // per-request dynamic keys on the serialize path and a handful of startup-time operation names
    // would only evict them.
    private static void ValidateUnboundOperationName(
        string operationName, bool isAction, bool explicitName, string paramName)
    {
        if (OpenTypeJsonOptions.IsValidDynamicPropertyName(operationName)) return;

        string method = isAction ? "AddAction" : "AddFunction";
        string kind = isAction ? "action" : "function";

        if (explicitName)
        {
            throw new ArgumentException(
                $"OhData: '{operationName}' is not a valid OData identifier, so it cannot name an " +
                $"unbound {kind}. The name is written into $metadata as the " +
                $"{(isAction ? "ActionImport" : "FunctionImport")} name and used as the route " +
                $"segment {(isAction ? "POST" : "GET")} /{{prefix}}/{operationName}. An identifier " +
                "is 1 to 128 characters: it starts with a Unicode letter or '_', and continues " +
                "with letters, digits, combining marks, connector punctuation or format characters " +
                "(CSDL 4.0 section 4.1, odataIdentifier).",
                paramName);
        }

        throw new ArgumentException(
            $"OhData: {method} derived the name '{operationName}' from the handler's method, and it " +
            $"is not a valid OData identifier, so it cannot name an unbound {kind}. This is what an " +
            "anonymous lambda or a local function produces -- the compiler generates an unspeakable " +
            $"name. Pass an explicit one: {method}(handler, \"My{(isAction ? "Action" : "Function")}\").",
            paramName);
    }

    internal void Register()
    {
        var capturedTypes = _profileTypes.ToList();
        var capturedUnbound = _unboundOps.ToList();
        string capturedPrefix = _prefix;
        string capturedName = _name;
        var capturedDefaults = _defaults;
        var capturedNamingPolicy = _jsonPropertyNamingPolicy;
        bool capturedOpenTypes = _openTypesEnabled;

        _services.AddKeyedSingleton<OhDataRegistration>(capturedName, (sp, _) =>
        {
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("OhData");
            var modelBuilder = new ODataConventionModelBuilder();
            var defaults = capturedDefaults;
            var profiles = new List<IEntitySetEndpointSource>();

            // Profiles are registered as scoped so they can safely inject scoped services
            // (e.g. DbContext). Create a temporary scope for the startup construction which
            // builds the EDM model and captures structural metadata. The scope is disposed
            // after registration completes — only structural properties are read afterwards.
            using var startupScope = sp.CreateScope();

            foreach (var type in capturedTypes)
            {
                object instance = startupScope.ServiceProvider.GetRequiredService(type);

                if (instance is IVisitModelBuilder vmb)
                {
                    logger?.LogDebug("OhData: building EDM for {Profile}", type.Name);
                    try
                    {
                        vmb.VisitModelBuilder(modelBuilder, defaults);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "OhData: failed to build EDM for profile {Profile}", type.Name);
                        throw new InvalidOperationException(
                            $"OhData: failed to build EDM for profile '{type.Name}'. See inner exception for details.", ex);
                    }
                }

                if (instance is IEntitySetEndpointSource source)
                {
                    profiles.Add(source);
                }
                else
                {
                    logger?.LogWarning(
                        "OhData: {Type} does not implement IEntitySetEndpointSource and will be skipped",
                        type.Name);
                }
            }

            var duplicates = profiles
                .GroupBy(s => s.EntitySetName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicates.Count > 0)
            {
                throw new InvalidOperationException(
                    $"OhData: duplicate entity set name(s) registered: {string.Join(", ", duplicates)}. " +
                    "Each entity set name must be unique within an OhData registration.");
            }

            // Startup validation (S5): unbound-operation route collisions. An unbound function
            // claims GET /{prefix}/{Name}; an unbound action claims POST /{prefix}/{Name}. Two
            // unbound operations of the same kind sharing a name -- or an unbound operation
            // sharing a name with an entity set that registers the same (route, HTTP method)
            // pair (a collection GET for functions, POST for actions) -- would otherwise only
            // surface as an AmbiguousMatchException at request time with zero startup
            // diagnostics. Comparisons are case-insensitive (OrdinalIgnoreCase), matching
            // ASP.NET Core's default route-template matching and the duplicate-entity-set-name
            // check above.
            var duplicateUnboundOps = capturedUnbound
                .GroupBy(o => (NormalizedName: o.Name.ToUpperInvariant(), o.IsAction))
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicateUnboundOps is not null)
            {
                string dupKind = duplicateUnboundOps.Key.IsAction ? "action" : "function";
                string dupName = duplicateUnboundOps.First().Name;
                throw new InvalidOperationException(
                    $"OhData: duplicate unbound {dupKind} name '{dupName}'. Each unbound " +
                    $"{dupKind} name must be unique within an OhData registration (route templates are " +
                    "case-insensitive).");
            }

            foreach (var op in capturedUnbound)
            {
                // #492 §1: HasCollectionGet, not `HasGetAll || HasGetQueryable`. The old test
                // enumerated two of the THREE collection-read paths, so a Priority-1 profile
                // (ODataEntitySetProfile with only GetODataQueryable) reported both false while
                // MapEntitySet registered its collection GET anyway -- and the colliding unbound
                // function sailed through startup to become an AmbiguousMatchException on every
                // collection read of that set. Asking the single "does a collection GET get
                // registered" question is what stops a fourth read path repeating it.
                var collidingProfile = profiles.FirstOrDefault(p =>
                    string.Equals(p.EntitySetName, op.Name, StringComparison.OrdinalIgnoreCase)
                    && (op.IsAction ? p.HasPost : p.HasCollectionGet));
                if (collidingProfile is not null)
                {
                    string opKind = op.IsAction ? "action" : "function";
                    string httpMethod = op.IsAction ? "POST" : "GET";
                    throw new InvalidOperationException(
                        $"OhData: unbound {opKind} '{op.Name}' conflicts with entity set " +
                        $"'{collidingProfile.EntitySetName}': both would register {httpMethod} /{op.Name} " +
                        "(route templates are case-insensitive). Rename the unbound " +
                        $"{opKind} (AddFunction/AddAction 'name' parameter) or the entity set.");
                }
            }

            // Gap 7: register unbound functions/actions in the EDM as FunctionImport/ActionImport
            // Must be done BEFORE GetEdmModel() so they appear in $metadata.
            foreach (var op in capturedUnbound)
            {
                if (!op.IsAction)
                {
                    var fn = modelBuilder.Function(op.Name);
                    RegisterUnboundOpReturnType(fn, op);
                    foreach (var param in op.Parameters)
                    {
                        var p = fn.Parameter(param.ParameterType, param.Name!);
                        if (param.IsOptional) p.Optional();
                    }
                    // #468: FunctionConfiguration defaults IncludeInServiceDocument to true, and
                    // OhData never touched it -- so $metadata asserted an advertisement the
                    // hand-built service document never made, and for a PARAMETERIZED import the
                    // claim is not even legal CSDL: EdmValidator flags
                    // FunctionImportWithParameterShouldNotBeIncludedInServiceDocument, because
                    // CSDL 4.0 section 13.6 reserves the service document for imports that can be
                    // invoked with nothing but their name. A parameterless import CAN be, and
                    // Microsoft.AspNetCore.OData's own service-document serializer honours the
                    // flag, so those keep it and MapAll now derives the service document from the
                    // EDM container -- flag and document come from one source, and the two can no
                    // longer disagree.
                    fn.IncludeInServiceDocument = op.Parameters.Length == 0;
                }
                else
                {
                    var act = modelBuilder.Action(op.Name);
                    RegisterUnboundOpReturnType(act, op);
                    foreach (var param in op.Parameters)
                    {
                        var p = act.Parameter(param.ParameterType, param.Name!);
                        if (param.IsOptional) p.Optional();
                    }
                }
            }

            // NEW-1 fix: navigation-target types (e.g. Tag, OrderLine) that are reached only
            // through a HasMany/HasOptional/HasRequired navigation -- never registered as their
            // own EntitySetProfile -- otherwise end up with no ModelBoundQuerySettings
            // annotation at all. Microsoft's model-bound validator (EdmHelpers.IsNotFilterable /
            // IsNotSortable / IsNotSelectable in Microsoft.AspNetCore.OData) treats every
            // property on an un-annotated type as NotFilterable/NotSortable/NotSelectable by
            // default, which made every nav-path $filter/$orderby/$select (e.g.
            // `tags/any(t: t/name eq 'X')`) 400 once ValidatePropertyAllowlists started calling
            // Validate() unconditionally (#141). Nav-target types have no allowlist surface of
            // their own -- only EntitySetProfile exposes FilterProperties/OrderByProperties/
            // SelectProperties/ExpandProperties, and that's only for the profile's own root
            // type -- so "fully permissive" is the only coherent semantics for them. Root
            // profile types are deliberately left untouched: EntitySetProfile.VisitModelBuilder
            // already called Filter(_filterProperties) etc. on them above, possibly with an
            // allowlist, and that must survive unmodified for the root type's own $filter.
            //
            // This must run from ODataConventionModelBuilder.OnModelCreating, which fires after
            // MapTypes() has discovered every reachable type (including nav targets) but before
            // the base builder writes the model-bound annotations into the IEdmModel.
            // StructuralTypes does NOT yet contain nav-target types at the point profiles finish
            // visiting the builder above -- they're discovered lazily inside GetEdmModel().
            var rootModelTypes = new HashSet<Type>(profiles.Select(p => p.ModelType));
            modelBuilder.OnModelCreating = b =>
            {
                // #253: rename every structural property whose CLR member carries a
                // [JsonPropertyName] so the EDM/$metadata/query-option name IS the JSON name. Runs
                // for EVERY structural type the builder discovered (root profile types AND nav-target
                // types reached only through navigation), so a nested $expand($select=renamedChild)
                // resolves against — and survives the strip under — the same name too.
                ApplyJsonPropertyNameRenames(b);
                MarkNavigationTargetTypesFullyQueryable(b, rootModelTypes);
            };

            var edmModel = modelBuilder.GetEdmModel();

            // #206/#303: advertise the runtime $expand gates per entity set as
            // Org.OData.Capabilities.V1 annotations, so a client can discover them from $metadata
            // before it 400s. Best-effort: the convention builder returns a concrete EdmModel (the
            // only type that accepts vocabulary annotations); if that ever changes, the annotations
            // are skipped rather than failing startup — every limit is still enforced at request time.
            AnnotateCapabilities(edmModel, profiles, logger);

            logger?.LogInformation(
                "OhData: initialized {Count} entity set(s) [{Names}] at prefix '{Prefix}'",
                profiles.Count,
                string.Join(", ", profiles.Select(p => p.EntitySetName)),
                capturedPrefix);

            var reg = new OhDataRegistration(
                capturedName, capturedPrefix, edmModel, profiles, capturedUnbound, capturedNamingPolicy, capturedOpenTypes,
                // #474: the server-wide write-body ceiling, carried onto the registration so the
                // group filter can apply it to the routes that belong to no entity set (the unbound
                // actions), which have no profile to resolve a limit from.
                defaults.MaxRequestBodyBytes);
            // Also register in the collection for named access
            sp.GetRequiredService<OhDataRegistrationCollection>().Add(capturedName, reg);
            return reg;
        });

        // Backwards compat: the default registration is also accessible as an unkeyed singleton
        if (capturedName == OhDataDefaults.DefaultRegistrationName)
        {
            _services.AddSingleton<OhDataRegistration>(sp =>
                sp.GetRequiredKeyedService<OhDataRegistration>(OhDataDefaults.DefaultRegistrationName));
        }
    }

    // #206/#303/#367: the single place OhData translates its runtime query-capability gates into
    // Org.OData.Capabilities.V1 vocabulary annotations on the EDM. One pass, one term per method, so
    // #367's wider set (FilterRestrictions / SortRestrictions / CountRestrictions / SelectSupport)
    // is a new call here rather than a second mechanism.
    //
    // ---------------------------------------------------------------------------------------------
    // WHAT IS AND IS NOT EXPRESSIBLE (#303). Read this before adding a numeric limit here.
    //
    // Org.OData.Capabilities.V1 contains exactly ONE numeric slot: `MaxLevels` (Edm.Int32), which
    // appears in ExpandRestrictionsType, FilterRestrictionsType, and the Insert/Update/Delete
    // restriction types. In every one of those it means a NESTING / TRAVERSAL DEPTH, never a count
    // of entities. There is NO term, at entity-set scope or navigation-property scope, that
    // expresses a maximum result count, page size, or $top ceiling.
    //
    // Verified two independent ways, both on 2026-08-23:
    //   (1) the Capabilities CSDL bundled in Microsoft.OData.Edm 8.4.0 — the exact package this
    //       repo resolves — dumped via CsdlWriter over CapabilitiesVocabularyModel.Instance;
    //   (2) the upstream OASIS source of truth,
    //       oasis-tcs/odata-vocabularies @ main, vocabularies/Org.OData.Capabilities.V1.xml.
    // Both agree: grep for a numeric-typed property returns MaxLevels and nothing else.
    //
    // Therefore:
    //   MaxExpansionDepth  -> EXPRESSIBLE as ExpandRestrictions/MaxLevels. Emitted below (#206).
    //   ExpandEnabled      -> EXPRESSIBLE as ExpandRestrictions/Expandable. Emitted below (#303).
    //   MaxExpandTop       -> NOT EXPRESSIBLE. It is a count of related entities per expanded
    //                         navigation, not a depth. TopSupported/SkipSupported are Core.Tag
    //                         booleans with no numeric slot, and advertising TopSupported=false
    //                         would be a lie (a nested $top IS supported, up to the ceiling).
    //                         Deliberately left unadvertised — see the note on inventing terms below.
    //   MaxExpandBreadth   -> NOT EXPRESSIBLE. A count of expansions across the tree; same reason.
    //   MaxTop (#367)      -> NOT EXPRESSIBLE, same reason as MaxExpandTop.
    //
    // We do NOT mint a custom `OhData.V1.*` term for the three inexpressible limits. A non-standard
    // annotation is not discoverable by any client that does not already know OhData, so it buys no
    // interoperability while implying some; and approximating with MaxLevels or TopSupported would
    // publish a statement that is simply false. They stay enforced at request time (400) and
    // documented, which is the honest outcome. Do not "fix" this by inventing a term.
    //
    // For reference, Microsoft.AspNetCore.OData 9.5.0 advertises NONE of these: a model built with
    // ODataConventionModelBuilder over a type carrying [Page(MaxTop=25, PageSize=10)],
    // [Expand(MaxDepth=2)], [Count], [Filter] and [OrderBy] emits zero vocabulary annotations — its
    // model-bound query settings are CLR-side annotations that never reach the CSDL. OhData is
    // already ahead of them here; matching their shape would mean emitting nothing at all.
    // ---------------------------------------------------------------------------------------------
    private static void AnnotateCapabilities(
        IEdmModel edmModel, IReadOnlyList<IEntitySetEndpointSource> profiles, ILogger? logger)
    {
        if (edmModel is not EdmModel model) return;
        IEdmEntityContainer? container = model.EntityContainer;
        if (container is null) return;

        (IEntitySetEndpointSource Profile, IEdmEntitySet EntitySet)[] targets = profiles
            .Select(profile => (Profile: profile, EntitySet: container.FindEntitySet(profile.EntitySetName)))
            .Where(pair => pair.EntitySet is not null)
            .Select(pair => (pair.Profile, pair.EntitySet!))
            .ToArray();
        if (targets.Length == 0) return;

        AnnotateExpandRestrictions(model, targets, logger);
    }

    // #206/#303: attach an Org.OData.Capabilities.V1.ExpandRestrictions vocabulary annotation to each
    // entity set, carrying the profile's RESOLVED (profile override falling back to
    // EntitySetDefaults) $expand gates:
    //
    //   Expandable = false  — only when $expand is disabled outright. `true` is the vocabulary's own
    //                         default, so emitting it would add bytes and assert nothing; omitting it
    //                         keeps every set that already advertised correctly byte-identical.
    //                         Without this an entity set with ExpandEnabled=false advertised
    //                         `MaxLevels=3` and nothing else — actively misleading, since it read as
    //                         "expand up to 3 levels" for a set that 400s every $expand (#367's
    //                         headline evidence).
    //   MaxLevels           — the resolved MaxExpansionDepth (#206). Emitted unconditionally,
    //                         including when Expandable=false: it is not false there, merely moot,
    //                         and keeping it unconditional is what makes this change a strict
    //                         addition rather than a rewrite of existing metadata.
    //
    // Property order follows the vocabulary's own declaration order (Expandable before MaxLevels).
    // Emitted inline (inside the EntitySet element) so a CSDL reader finds it without an
    // out-of-line lookup. Best-effort and non-fatal: a missing term is skipped (the gates are still
    // enforced by the request-time validator regardless).
    private static void AnnotateExpandRestrictions(
        EdmModel model,
        IReadOnlyList<(IEntitySetEndpointSource Profile, IEdmEntitySet EntitySet)> targets,
        ILogger? logger)
    {
        IEdmTerm? term = CapabilitiesVocabularyModel.Instance
            .FindDeclaredTerm("Org.OData.Capabilities.V1.ExpandRestrictions");
        if (term is null) return;

        foreach ((IEntitySetEndpointSource profile, IEdmEntitySet entitySet) in targets)
        {
            var properties = new List<EdmPropertyConstructor>(2);
            if (!profile.ExpandEnabled)
                properties.Add(new EdmPropertyConstructor("Expandable", new EdmBooleanConstant(false)));
            properties.Add(new EdmPropertyConstructor("MaxLevels", new EdmIntegerConstant(profile.MaxExpansionDepth)));

            var annotation = new EdmVocabularyAnnotation(entitySet, term, new EdmRecordExpression(properties));
            annotation.SetSerializationLocation(model, EdmVocabularyAnnotationSerializationLocation.Inline);
            model.AddVocabularyAnnotation(annotation);
        }

        logger?.LogDebug("OhData: advertised ExpandRestrictions for {Count} entity set(s).", targets.Count);
    }

    // Marks every structural type the builder discovered that is NOT one of the root profiles'
    // own entity types (i.e. reached only via navigation) as fully filterable/sortable/
    // selectable/expandable/countable. Filter()/OrderBy()/Select()/Expand()/Count() as seen on
    // EntitySetProfile are convenience members of the *generic* StructuralTypeConfiguration
    // <TStructuralType> wrapper (EntityTypeConfiguration<T>/ComplexTypeConfiguration<T>), which
    // OhData never constructs for nav-target types. builder.StructuralTypes instead yields the
    // plain, non-generic StructuralTypeConfiguration the convention builder itself allocated for
    // every discovered type (see ODataModelBuilder.AddEntityType/AddComplexType) -- that base
    // type has no Filter()/OrderBy()/etc. overloads at all, only the QueryConfiguration property
    // those generic wrapper methods delegate to, so we call it directly instead.
    //
    // #296: a type can be BOTH a root profile's own entity type AND some navigation's target type
    // (the self-referential case -- e.g. LvNode.Children : List<LvNode> -- but also the more general
    // "shared type" case where entity A's navigation targets entity B's own root type). The original
    // `!rootModelTypes.Contains(...)` filter excluded every such type from this method entirely, on
    // the theory that root types must keep whatever Filter/OrderBy/Select/Count/Expand allowlist their
    // own profile configured above (true) -- but that filter ALSO skipped the `SetMaxTop(null)` call,
    // which is the ONLY setting here that has nothing to do with the root type's own request-level
    // MaxTop. Microsoft's SelectExpandQueryValidator.ValidateNestedTop reads the model-bound MaxTop of
    // the NAVIGATION'S TARGET TYPE (not the root type reached via GET /Set) to police a nested $top
    // inside $expand=Nav($top=N). For a root+nav-target type that model-bound MaxTop was left at its
    // implicit default of 0 (created as a side effect of the root profile's own Filter()/Select()/etc.
    // calls), so every nested $top against such a type 400'd before OhData's own MaxExpandTop ceiling
    // (ValidateNestedTopCeiling, enforced separately in OhDataEndpointFactory) ever ran.
    //
    // Fix: compute the set of types that are SOMEONE's navigation target (self or cross-referenced),
    // and clear model-bound MaxTop on every one of them -- root or not. Root types that are ALSO nav
    // targets get ONLY the MaxTop clear; their Filter/OrderBy/Select/Count/Expand configuration (set by
    // EntitySetProfile.VisitModelBuilder above, possibly a restrictive allowlist) is left completely
    // untouched, so the root entity set's OWN request-level MaxTop/query-capability behavior (governed
    // at runtime by IEntitySetEndpointSource.MaxTop, not by this model-bound setting) is unaffected.
    // Pure nav-target-only types keep the existing "fully permissive" treatment unchanged.
    private static void MarkNavigationTargetTypesFullyQueryable(
        ODataModelBuilder builder, HashSet<Type> rootModelTypes)
    {
        var structuralTypes = builder.StructuralTypes.ToList();

        var navigationTargetTypes = new HashSet<Type>(
            structuralTypes
                .SelectMany(stc => stc.NavigationProperties)
                .Select(np => np.RelatedClrType)
                .Where(t => t is not null)!);

        foreach (var stc in structuralTypes)
        {
            bool isRoot = rootModelTypes.Contains(stc.ClrType);
            bool isNavTarget = navigationTargetTypes.Contains(stc.ClrType);
            if (isRoot && !isNavTarget) continue; // pure root type: leave entirely untouched

            var query = stc.QueryConfiguration;

            if (!isRoot)
            {
                // Pure nav-target-only type (never its own entity set): no allowlist surface of its
                // own, so "fully permissive" is the only coherent semantics -- unchanged from before.
                query.SetFilter(properties: null, enableFilter: true);
                query.SetOrderBy(properties: null, enableOrderBy: true);
                query.SetSelect(properties: null, selectType: SelectExpandType.Allowed);
                query.SetCount(enableCount: true);
                // A generous (not unbounded) max expand depth, consistent with the other
                // effectively-unlimited-but-not-infinite settings this framework uses elsewhere
                // (e.g. MaxAnyAllExpressionDepth = 1000 in OhDataEndpointFactory).
                query.SetExpand(properties: null, maxDepth: 1000, expandType: SelectExpandType.Allowed);
            }

            // #206 phase 2 (optioned expand) / #296 (root+nav-target types): once ANY model-bound
            // setting exists on a type, Microsoft's SelectExpand validator defaults its MaxTop to 0,
            // which rejects a nested $top inside a $expand of THIS type ($expand=Children($top=N))
            // with "limit of 0 for Top". Nav-target types (root or not) are the collection element
            // types a nested $top pages, so clear that spurious ceiling (null = unlimited) on all of
            // them. OhData governs $top itself: the root path clamps to source.MaxTop, and the
            // expand-pushdown path applies the nested $top directly, bounded by source.MaxExpandTop
            // (ValidateNestedTopCeiling) -- so clearing this model-bound MaxTop does not make nested
            // $top unbounded, it just stops Microsoft's validator from pre-empting OhData's own check.
            query.SetMaxTop(null);
        }
    }

    // #253: give every property carrying a [System.Text.Json.Serialization.JsonPropertyName] that
    // attribute's value as its EDM name, so $metadata, $select/$filter/$orderby/$expand, the nav-path
    // URL segments and the response payload all use one name per property. This applies UNIFORMLY to
    // structural AND navigation properties (#253 completion — reverses #184): a renamed navigation is
    // addressed by its JSON name on every OData surface, exactly like a renamed structural property
    // (its JSON payload key, produced by ResolveNavigationJsonKey off the same [JsonPropertyName], then
    // agrees with the EDM/$expand identifier). Fails fast when two properties on one type would resolve
    // to the same EDM name (case-insensitive, the way OData resolves identifiers) — the collision check
    // now spans navigations and structural properties together — since that ambiguity cannot be
    // represented in the EDM or the URL space.
    private static void ApplyJsonPropertyNameRenames(ODataModelBuilder builder)
    {
        foreach (StructuralTypeConfiguration structuralType in builder.StructuralTypes)
        {
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Every property — structural and navigation alike — is renamed to its [JsonPropertyName].
            foreach (PropertyConfiguration property in structuralType.Properties)
            {
                string edmName = property.PropertyInfo is { } clr
                    ? ODataPropertyNaming.ResolveEdmName(clr)
                    : property.Name;
                if (!string.Equals(edmName, property.Name, StringComparison.Ordinal))
                    property.Name = edmName;

                if (seen.TryGetValue(edmName, out string? existing) &&
                    !string.Equals(existing, property.PropertyInfo?.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"OhData: type '{structuralType.ClrType.Name}' has two properties that resolve to " +
                        $"the same OData name '{edmName}' ('{existing}' and " +
                        $"'{property.PropertyInfo?.Name ?? edmName}'). A [JsonPropertyName] rename must not " +
                        "collide with another property's OData name. Rename one of them.");
                }
                seen[edmName] = property.PropertyInfo?.Name ?? edmName;
            }
        }
    }

    private static void RegisterUnboundOpReturnType(FunctionConfiguration fn, UnboundOperationDefinition op)
    {
        if (op.ReturnType is null) return;
        var refl = op.ReturnsCollection
            ? typeof(FunctionConfiguration).GetMethod(nameof(FunctionConfiguration.ReturnsCollection), System.Array.Empty<Type>())!
                  .MakeGenericMethod(op.ReturnType)
            : typeof(FunctionConfiguration).GetMethod(nameof(FunctionConfiguration.Returns), System.Array.Empty<Type>())!
                  .MakeGenericMethod(op.ReturnType);
        refl.Invoke(fn, null);
    }

    private static void RegisterUnboundOpReturnType(ActionConfiguration act, UnboundOperationDefinition op)
    {
        if (op.ReturnType is null) return;
        var refl = op.ReturnsCollection
            ? typeof(ActionConfiguration).GetMethod(nameof(ActionConfiguration.ReturnsCollection), System.Array.Empty<Type>())!
                  .MakeGenericMethod(op.ReturnType)
            : typeof(ActionConfiguration).GetMethod(nameof(ActionConfiguration.Returns), System.Array.Empty<Type>())!
                  .MakeGenericMethod(op.ReturnType);
        refl.Invoke(act, null);
    }
}
