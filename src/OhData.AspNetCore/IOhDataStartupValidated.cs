namespace OhData;

/// <summary>
/// A service whose construction validates configuration, resolved by <c>MapOhData</c> so the
/// failure lands at startup rather than on the first request.
/// </summary>
/// <remarks>
/// <para>
/// The marker carries no members: resolving it is the whole point, because the validation happens
/// in the DI factory that builds it.
/// </para>
/// <para>
/// It exists because <c>MapOhData</c> used to force <c>IDeltaFactory</c> by name, which stopped
/// being possible when delta mapping moved to its own package (#665). Internal rather than public
/// — the core has exactly one consumer for it, and a public extensibility point nobody asked for is
/// surface that has to be supported forever.
/// </para>
/// </remarks>
internal interface IOhDataStartupValidated
{
}
