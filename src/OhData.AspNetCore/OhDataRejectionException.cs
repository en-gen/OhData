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

    internal OhDataResult Result { get; }
}
