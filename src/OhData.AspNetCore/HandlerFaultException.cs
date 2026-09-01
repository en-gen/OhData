using System;

namespace OhData;

/// <summary>
/// #496 finding 4: marks an exception as having come out of USER code — a profile's handler
/// delegate or navigation delegate — so a route's narrow, framework-facing <c>catch</c> clauses
/// cannot claim it as a statement about the request.
/// </summary>
/// <remarks>
/// Each read route wraps its whole body in a <c>try</c> whose
/// <c>catch (Microsoft.OData.ODataException)</c> answers <c>400 InvalidQueryOption</c> with
/// <c>ex.Message</c> passed VERBATIM to the client. That catch is load-bearing and cannot simply be
/// narrowed by scope: <c>Microsoft.AspNetCore.OData</c> parses most option values lazily, and the
/// framework itself deliberately THROWS <c>ODataException</c> from deep inside
/// <c>ApplyCollectionPipelineAsync</c> (<c>NestedWindowRejection</c>,
/// <c>EnsureWithinExpandCeiling</c>) precisely because these clauses convert it to a 400.
/// <para>
/// But that same <c>try</c> also encloses handler invocation, so a handler proxying a downstream
/// OData service — the realistic way user code raises this type — turned a server-side dependency
/// fault into a client-blamed <c>400</c> carrying the handler's own message, which is a targeted
/// bypass of the rule that no internal exception message reaches the client. The
/// <c>FormatException</c> clause on every keyed route had the same shape one severity band down
/// (see <see cref="ODataKeyFormatException"/>, which fixes that half structurally instead).
/// </para>
/// <para>
/// The direction of the fix matters. Guarding the FRAMEWORK's option-touching calls instead would
/// mean enumerating them, and a missed one turns a legitimate 400 into a 500 — it fails in the
/// wrong direction. Marking user code fails in the right one: a seam nobody wrapped simply behaves
/// as it did before. So the marker is applied at handler seams via
/// <c>OhDataEndpointFactory.AsHandlerFault</c>, and the group-level exception filter unwraps it so
/// the operator's log still carries the real exception rather than this envelope.
/// </para>
/// </remarks>
internal sealed class HandlerFaultException : Exception
{
    public HandlerFaultException(Exception inner)
        : base("A profile handler threw. See the inner exception.", inner)
    {
    }

    /// <summary>
    /// The exception types a read route's narrow catches would otherwise MISCLASSIFY as a client
    /// error. Nothing else is wrapped — an ordinary handler fault already reaches the group filter
    /// untouched, and wrapping it would only add a frame to the log. In particular
    /// <see cref="OperationCanceledException"/> is never wrapped: #493 makes the group filter's
    /// decline a question about <c>HttpContext.RequestAborted</c> AND the exception type, and a
    /// wrapper would defeat the type half.
    /// </summary>
    public static bool IsMisclassifiable(Exception ex) =>
        ex is Microsoft.OData.ODataException or FormatException;

    /// <summary>Unwraps the marker for logging; returns <paramref name="ex"/> unchanged otherwise.</summary>
    public static Exception Unwrap(Exception ex) =>
        ex is HandlerFaultException { InnerException: { } inner } ? inner : ex;
}
