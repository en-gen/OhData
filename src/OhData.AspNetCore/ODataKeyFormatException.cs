using System;

namespace OhData;

/// <summary>
/// #496 finding 4: the exception <see cref="ODataKeyParser"/> raises for a key segment it cannot
/// parse, and the only thing a keyed route's <c>catch</c> maps to
/// <c>400 "Invalid key format for &lt;set&gt;: '&lt;key&gt;'"</c>.
/// </summary>
/// <remarks>
/// Every keyed route wraps its WHOLE body in that <c>catch</c> — the key parse, the handler
/// invocation, the ETag delegates and serialization all sit inside it — so while the clause caught
/// a bare <see cref="FormatException"/> a handler that threw one from its own body (a downstream
/// parse, a <c>decimal.Parse</c> on a CSV column) was answered with a <c>400</c> asserting the
/// client's key was malformed, for a request whose key had parsed cleanly one line earlier. The
/// route's own <c>catch</c> was wider than the condition its message describes.
/// <para>
/// Narrowing the clause to this type is structural rather than positional: the routes keep their
/// single whole-body <c>try</c> (splitting them would need one <c>try</c> per parse site across
/// eighteen routes, and a missed site would silently 500 a genuinely bad key), and the ONLY thing
/// that can raise this type is <see cref="ODataKeyParser.Parse"/>'s single throw site. A handler's
/// own <see cref="FormatException"/> now reaches the group filter and becomes a logged 500 — which
/// is what a server-side fault is.
/// </para>
/// <para>
/// It derives from <see cref="FormatException"/> deliberately: a custom key type's
/// <c>TypeConverter</c> is user code that legitimately signals "not parseable as this type" that
/// way, <see cref="ODataKeyParser"/> already funnels it into its own throw, and any caller outside
/// this assembly that catches <see cref="FormatException"/> keeps working.
/// </para>
/// </remarks>
internal sealed class ODataKeyFormatException : FormatException
{
    public ODataKeyFormatException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
