using System;

namespace OhData;

/// <summary>
/// Carries a mapped <see cref="OhDataResult"/> from the seam that resolved it out to the group
/// filter that writes the envelope (#581).
/// </summary>
/// <remarks>
/// <para>
/// Internal on purpose. It is a transport, not a third way for user code to reject a request —
/// profiles declare rejections through <c>ConfigureExceptions</c>, and (once the handler
/// signatures carry it) by returning one.
/// </para>
/// <para>
/// Deliberately NOT derived from <c>Microsoft.OData.ODataException</c>. Every read route carries a
/// narrow <c>catch (ODataException)</c> that answers 400 with the exception's own message, and it
/// sits inside the route handler — below the group filter — so a rejection deriving from it would
/// be relabelled a 400 carrying the handler's text, which is the disclosure #496 finding 4 closed.
/// </para>
/// </remarks>
internal sealed class OhDataRejectionException : Exception
{
    internal OhDataRejectionException(OhDataResult result, Exception inner)
        : base(result.Message, inner) => Result = result;

    /// <summary>
    /// A rejection the handler RETURNED rather than one mapped from a throw. There is no inner
    /// exception, and the group filter uses that to tell the two apart: a mapped fault is logged at
    /// Warning because it was reclassified, a returned rejection at Debug because it is an ordinary
    /// outcome the handler chose.
    /// <para>
    /// Using an exception to carry it is an internal transport, not control flow the adopter writes:
    /// the handler returns a value. It buys one translation point shared with the mapping path, and
    /// the cost lands only on responses that were going to be errors anyway.
    /// </para>
    /// </summary>
    internal OhDataRejectionException(OhDataResult result)
        : base(result.Message) => Result = result;

    internal OhDataResult Result { get; }
}
