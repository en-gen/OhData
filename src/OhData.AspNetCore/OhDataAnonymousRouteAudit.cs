using System;

namespace OhData;

/// <summary>
/// #487: endpoint metadata marking a route that OhData mapped with <b>no authorization requirement
/// of its own</b>, inside a registration that demonstrably intends authorization somewhere else.
/// Purely diagnostic — nothing reads it at request time and its presence changes no behaviour.
/// <para>
/// It exists because the question "is this route actually anonymous?" cannot be answered where the
/// route is mapped. <c>MapOhData()</c> returns the group and the host applies its own
/// <c>RequireAuthorization()</c> to it <i>afterwards</i>, so at map time a route with no rule of its
/// own is indistinguishable from one that is about to inherit the host's backstop — which is the
/// documented, correct configuration. The audit is therefore attached at map time (where all the
/// static reasoning is available) and <i>read</i> from an
/// <see cref="Microsoft.AspNetCore.Builder.IEndpointConventionBuilder.Finally"/> convention, which
/// runs after every convention including the host's. See
/// <c>OhDataEndpointFactory.AttachAnonymousRouteAudit</c>.
/// </para>
/// </summary>
/// <param name="Key">
/// Dedupe key. One warning per subject, not per route: a category covers a dozen endpoints and an
/// entity set's <c>Invoke</c> category covers every bound operation on it.
/// </param>
/// <param name="Subject">What is anonymous, e.g. <c>the Invoke (bound function/action) routes of entity set 'Orders'</c>.</param>
/// <param name="Detail">Why it is anonymous — the configuration that produced it.</param>
/// <param name="Remedy">What to write to close it, or to state that it is intended.</param>
internal sealed record OhDataAnonymousRouteAudit(
    string Key,
    string Subject,
    string Detail,
    string Remedy);
