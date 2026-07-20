using System.Collections.Generic;
using OhData.Abstractions;

namespace OhData.AspNetCore;

/// <summary>
/// Endpoint metadata carrying the resolved per-operation authorization requirements (#199) for a
/// single OhData route, as structured <see cref="AuthRequirement"/> data rather than an opaque
/// policy. Attached by <c>OhDataEndpointFactory</c> to every route that a per-operation
/// authorization rule secures. Consumed by the opt-in OpenAPI/NSwag "auth requirements" filters
/// (#220) to render a human-readable requirements section in the operation's description.
/// </summary>
/// <remarks>
/// Only present when the profile used <c>ConfigureAuthorization(...)</c> (the auth-as-data model);
/// the legacy profile-wide <c>RequireAuthorization()</c>/<c>RequireRoles()</c> model carries no
/// structured requirement data and therefore attaches no metadata. Anonymous routes attach none.
/// </remarks>
/// <param name="Requirements">
/// The AND-combined requirements for the route's operation category. Never empty.
/// </param>
public sealed record OhDataOperationAuthMetadata(IReadOnlyList<AuthRequirement> Requirements);
