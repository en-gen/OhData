using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using Microsoft.OData.Edm.Vocabularies;
using Microsoft.OData.Edm.Vocabularies.V1;
using Microsoft.OData.ModelBuilder;
using OhData.Abstractions;

namespace OhData.AspNetCore;

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
    /// Registers an entity set profile. The profile is resolved from DI (scoped),
    /// so constructor injection of scoped services (e.g. DbContext) is supported.
    /// </summary>
    public OhDataBuilder AddEntitySetProfile<TProfile>() where TProfile : class, IEntitySetProfile
    {
        if (_profileTypes.Contains(typeof(TProfile)))
        {
            throw new InvalidOperationException(
                $"OhData: profile type '{typeof(TProfile).Name}' is already registered. Remove the duplicate AddEntitySetProfile call.");
        }

        // Detect the same profile type being registered in a different OhData registration.
        // Retrieve the tracker directly from the registered descriptors to avoid requiring a built container.
        var registryDescriptor = _services.FirstOrDefault(s => s.ServiceType == typeof(GlobalProfileRegistry));
        if (registryDescriptor?.ImplementationInstance is GlobalProfileRegistry registry)
        {
            if (!registry.RegisteredTypes.Add(typeof(TProfile)))
            {
                throw new InvalidOperationException(
                    $"Profile type '{typeof(TProfile).Name}' has already been registered in a different " +
                    "OhData registration. A profile type cannot be shared across registrations.");
            }
        }

        _services.AddScoped<TProfile>();
        _profileTypes.Add(typeof(TProfile));
        return this;
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

    private void AddProfileType(Type type)
    {
        if (_profileTypes.Contains(type)) return;
        // If another OhData registration already registered this profile type as a singleton,
        // skip re-registering it in DI but still track it for this builder's route mapping.
        if (!_services.Any(s => s.ServiceType == type))
            _services.AddScoped(type);
        _profileTypes.Add(type);
    }

    /// <summary>
    /// Registers an unbound function at the service root: <c>GET /prefix/{name}</c>.
    /// Parameters are read from query-string values; <see cref="System.Threading.CancellationToken"/>
    /// is detected and injected automatically if present.
    /// </summary>
    /// <param name="handler">The function delegate. The method name is used as the route segment unless <paramref name="name"/> is specified.</param>
    /// <param name="name">Optional explicit route name. Use when passing lambdas to override the compiler-generated name.</param>
    public OhDataBuilder AddFunction(Delegate handler, string? name = null)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        var op = UnboundOperationDefinition.From(handler, isAction: false);
        if (name is not null) op = op with { Name = name };
        _unboundOps.Add(op);
        return this;
    }

    /// <summary>
    /// Registers an unbound action at the service root: <c>POST /prefix/{name}</c>.
    /// Parameters are read from the JSON request body; <see cref="System.Threading.CancellationToken"/>
    /// is detected and injected automatically if present.
    /// </summary>
    /// <param name="handler">The action delegate. The method name is used as the route segment unless <paramref name="name"/> is specified.</param>
    /// <param name="name">Optional explicit route name. Use when passing lambdas to override the compiler-generated name.</param>
    public OhDataBuilder AddAction(Delegate handler, string? name = null)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        var op = UnboundOperationDefinition.From(handler, isAction: true);
        if (name is not null) op = op with { Name = name };
        _unboundOps.Add(op);
        return this;
    }

    internal void Register()
    {
        var capturedTypes = _profileTypes.ToList();
        var capturedUnbound = _unboundOps.ToList();
        string capturedPrefix = _prefix;
        string capturedName = _name;
        var capturedDefaults = _defaults;

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
                var collidingProfile = profiles.FirstOrDefault(p =>
                    string.Equals(p.EntitySetName, op.Name, StringComparison.OrdinalIgnoreCase)
                    && (op.IsAction ? p.HasPost : (p.HasGetAll || p.HasGetQueryable)));
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
            modelBuilder.OnModelCreating = b => MarkNavigationTargetTypesFullyQueryable(b, rootModelTypes);

            var edmModel = modelBuilder.GetEdmModel();

            // #206: advertise the resolved MaxExpansionDepth per entity set as the
            // Org.OData.Capabilities.V1.ExpandRestrictions/MaxLevels annotation, so a client can
            // discover the server's $expand/$levels ceiling from $metadata before it 400s a too-deep
            // request. Best-effort: the convention builder returns a concrete EdmModel (the only type
            // that accepts vocabulary annotations); if that ever changes, the annotation is skipped
            // rather than failing startup — the limit is still enforced at request time.
            AnnotateExpandRestrictions(edmModel, profiles, logger);

            logger?.LogInformation(
                "OhData: initialized {Count} entity set(s) [{Names}] at prefix '{Prefix}'",
                profiles.Count,
                string.Join(", ", profiles.Select(p => p.EntitySetName)),
                capturedPrefix);

            var reg = new OhDataRegistration(capturedPrefix, edmModel, profiles, capturedUnbound);
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

    // #206: attach an Org.OData.Capabilities.V1.ExpandRestrictions vocabulary annotation carrying
    // MaxLevels = the profile's resolved MaxExpansionDepth to each entity set, so the ceiling is
    // discoverable from $metadata. Emitted inline (inside the EntitySet element) so a CSDL reader
    // finds it without an out-of-line lookup. Best-effort and non-fatal: any missing model/term/set
    // is skipped (the depth limit is still enforced by the request-time validator regardless).
    private static void AnnotateExpandRestrictions(
        IEdmModel edmModel, IReadOnlyList<IEntitySetEndpointSource> profiles, ILogger? logger)
    {
        if (edmModel is not EdmModel model) return;
        IEdmTerm? term = CapabilitiesVocabularyModel.Instance
            .FindDeclaredTerm("Org.OData.Capabilities.V1.ExpandRestrictions");
        if (term is null) return;

        IEdmEntityContainer? container = model.EntityContainer;
        if (container is null) return;

        foreach ((IEntitySetEndpointSource profile, IEdmEntitySet entitySet) in profiles
            .Select(profile => (profile, entitySet: container.FindEntitySet(profile.EntitySetName)))
            .Where(pair => pair.entitySet is not null)
            .Select(pair => (pair.profile, pair.entitySet!)))
        {
            var record = new EdmRecordExpression(
                new EdmPropertyConstructor("MaxLevels", new EdmIntegerConstant(profile.MaxExpansionDepth)));
            var annotation = new EdmVocabularyAnnotation(entitySet, term, record);
            annotation.SetSerializationLocation(model, EdmVocabularyAnnotationSerializationLocation.Inline);
            model.AddVocabularyAnnotation(annotation);
        }

        logger?.LogDebug("OhData: advertised ExpandRestrictions/MaxLevels for {Count} entity set(s).", profiles.Count);
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
    private static void MarkNavigationTargetTypesFullyQueryable(
        ODataModelBuilder builder, HashSet<Type> rootModelTypes)
    {
        foreach (var query in builder.StructuralTypes
            .Where(stc => !rootModelTypes.Contains(stc.ClrType))
            .Select(stc => stc.QueryConfiguration))
        {
            query.SetFilter(properties: null, enableFilter: true);
            query.SetOrderBy(properties: null, enableOrderBy: true);
            query.SetSelect(properties: null, selectType: SelectExpandType.Allowed);
            query.SetCount(enableCount: true);
            // A generous (not unbounded) max expand depth, consistent with the other
            // effectively-unlimited-but-not-infinite settings this framework uses elsewhere
            // (e.g. MaxAnyAllExpressionDepth = 1000 in OhDataEndpointFactory).
            query.SetExpand(properties: null, maxDepth: 1000, expandType: SelectExpandType.Allowed);
            // #206 phase 2 (optioned expand): once ANY model-bound setting exists on a type,
            // Microsoft's SelectExpand validator defaults its MaxTop to 0, which rejects a nested
            // $top inside a $expand of THIS type ($expand=Children($top=N)) with "limit of 0 for
            // Top". Nav-target types are the collection element types a nested $top pages, so clear
            // that spurious ceiling (null = unlimited). OhData governs $top itself: the root path
            // clamps to source.MaxTop, and the expand-pushdown path applies the nested $top directly.
            query.SetMaxTop(null);
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
