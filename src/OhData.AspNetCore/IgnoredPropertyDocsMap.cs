using System;
using System.Collections.Generic;
using OhData.Abstractions;

namespace OhData.AspNetCore;

/// <summary>
/// Builds the CLR-model-type → ignored-CLR-property-name map the OpenAPI companion packages
/// (Microsoft.AspNetCore.OpenApi, NSwag, Swashbuckle) consult to omit properties excluded via
/// <c>EntitySetProfile.Ignore(...)</c> (#226) from generated schemas, so documents match the real
/// wire shape (#228). Exposed to the companion assemblies via <c>InternalsVisibleTo</c> so the
/// core package keeps carrying no doc-stack dependency.
/// </summary>
/// <remarks>
/// Within one registration, profiles sharing a model type are already validated to declare
/// identical ignore sets (see <see cref="IgnoredPropertyJsonOptions.BuildIgnoredPropertyMap"/>),
/// so per-registration merging is a no-op. Across registrations the sets may legitimately differ
/// (v2 may expose a property v1 ignores), but an OpenAPI document holds one component schema per
/// CLR type — this map takes the <b>union</b>, preferring to under-document a property one
/// registration exposes over listing a name another registration deliberately hides.
/// </remarks>
internal static class IgnoredPropertyDocsMap
{
    private static readonly IReadOnlyDictionary<Type, IReadOnlySet<string>> s_empty =
        new Dictionary<Type, IReadOnlySet<string>>();

    /// <summary>
    /// Unions <see cref="IEntitySetEndpointSource.IgnoredPropertyNames"/> by model type across
    /// every resolved registration. Returns an empty map when <paramref name="registrations"/> is
    /// <c>null</c> (OhData not registered in the host) or nothing is ignored.
    /// </summary>
    internal static IReadOnlyDictionary<Type, IReadOnlySet<string>> Build(
        OhDataRegistrationCollection? registrations)
    {
        if (registrations is null) return s_empty;

        Dictionary<Type, IReadOnlySet<string>>? result = null;
        foreach (OhDataRegistration registration in registrations.All)
        {
            foreach (IEntitySetEndpointSource profile in registration.Profiles)
            {
                if (profile.IgnoredPropertyNames.Count == 0) continue;
                result ??= new Dictionary<Type, IReadOnlySet<string>>();
                if (result.TryGetValue(profile.ModelType, out IReadOnlySet<string>? existing))
                {
                    var merged = new HashSet<string>(existing, StringComparer.Ordinal);
                    merged.UnionWith(profile.IgnoredPropertyNames);
                    result[profile.ModelType] = merged;
                }
                else
                {
                    result[profile.ModelType] = new HashSet<string>(
                        profile.IgnoredPropertyNames, StringComparer.Ordinal);
                }
            }
        }

        return result ?? s_empty;
    }
}
