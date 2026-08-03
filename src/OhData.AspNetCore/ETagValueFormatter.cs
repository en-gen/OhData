using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;

namespace OhData;

/// <summary>
/// Turns an ETag input value into the exact bytes that get fed to the hash (#351).
/// <para>
/// Two properties define this type. First, <b>round-trip fidelity</b>: the formatted text must
/// preserve every bit of state the value carries, so that two genuinely different entity states
/// can never produce the same hash input. A bare <c>ToString()</c> fails this for every date/time
/// type — it renders a general (human) pattern that drops the fractional second (and, for
/// <see cref="TimeOnly"/>, the seconds as well), so two writes inside the same second hash
/// identically and a stale <c>If-Match</c> passes the precondition it should have failed.
/// Second, <b>culture invariance</b>: the same entity state must produce the same ETag on every
/// server regardless of thread culture. A bare <c>ToString()</c> fails this too — it uses
/// <see cref="CultureInfo.CurrentCulture"/>, so a decimal renders as <c>1234,56</c> under
/// <c>de-DE</c> and <c>1234.56</c> under <c>en-US</c>, a <see cref="double"/> infinity renders as
/// <c>∞</c> under <c>de-DE</c>, and a <see cref="DateTime"/> renders in the Buddhist calendar
/// under <c>th-TH</c>.
/// </para>
/// <para>
/// The correct round-trip specifier is type-dependent and cannot be applied blindly: <c>"O"</c>
/// is only valid for <see cref="DateTime"/>, <see cref="DateTimeOffset"/>, <see cref="DateOnly"/>
/// and <see cref="TimeOnly"/>. It throws <see cref="FormatException"/> on
/// <see cref="TimeSpan"/>, <see cref="Guid"/>, <see cref="double"/>, <see cref="float"/>,
/// <see cref="decimal"/> and every integer type. Hence the per-category table below.
/// </para>
/// </summary>
internal static class ETagValueFormatter
{
    /// <summary>Frame tag for a <see langword="null"/> value — no payload follows.</summary>
    internal const byte TagNull = 0x00;

    /// <summary>Frame tag for a <see cref="byte"/> array — one length-prefixed raw payload follows.</summary>
    internal const byte TagBinary = 0x01;

    /// <summary>
    /// Frame tag for any other value — two length-prefixed UTF-8 payloads follow: the CLR type
    /// discriminator, then the invariant round-trip text.
    /// </summary>
    internal const byte TagText = 0x02;

    // UTF-8 type-discriminator bytes, cached per CLR type so the ETag path does not re-encode a
    // constant string on every response. Bounded by the number of distinct ETag property types.
    private static readonly ConcurrentDictionary<Type, byte[]> s_typeTags = new();

    /// <summary>
    /// Formats <paramref name="value"/> round-trippably and culture-invariantly.
    /// </summary>
    /// <remarks>
    /// Per category, and why:
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="DateTime"/>, <see cref="DateTimeOffset"/>, <see cref="DateOnly"/>,
    /// <see cref="TimeOnly"/> — <c>"O"</c>, the ISO-8601 round-trip pattern. Fixed-format and
    /// therefore culture-proof (a <see cref="CultureInfo"/> is still passed for clarity); carries
    /// all seven fractional-second digits, and for <see cref="DateTime"/> also the
    /// <see cref="DateTimeKind"/> (via the <c>Z</c>/offset suffix or its absence). This is the
    /// category the reported lost-update came from.
    /// </description></item>
    /// <item><description>
    /// <see cref="TimeSpan"/> — <c>"c"</c>, the invariant "constant" pattern. <c>"O"</c> is
    /// <b>not</b> a valid <see cref="TimeSpan"/> specifier and throws. <c>"c"</c> is defined to
    /// ignore culture and emits full tick precision.
    /// </description></item>
    /// <item><description>
    /// <see cref="Guid"/> — <c>"D"</c>. <c>"O"</c> is not a valid <see cref="Guid"/> specifier and
    /// throws; <c>"D"</c> is the canonical hyphenated form and is lossless (all 16 bytes).
    /// </description></item>
    /// <item><description>
    /// <see cref="double"/>, <see cref="float"/> — default formatting under
    /// <see cref="CultureInfo.InvariantCulture"/>. Since .NET Core 3.0 the default is the
    /// *shortest round-trippable* representation, which is strictly better than the legacy
    /// <c>"R"</c> (shorter output, same guarantee: re-parsing yields the identical bit pattern,
    /// including for <c>-0</c>, denormals and <see cref="double.MaxValue"/>). Invariant culture
    /// is what pins the decimal point and the <c>Infinity</c> spelling.
    /// </description></item>
    /// <item><description>
    /// <see cref="decimal"/> — default formatting under invariant culture. <see cref="decimal"/>
    /// has no binary-rounding problem: the default form prints every significant digit and
    /// preserves scale (<c>1.50m</c> → <c>"1.50"</c>, distinct from <c>1.5m</c> → <c>"1.5"</c>),
    /// so it is already round-trippable. Only the separator needed pinning.
    /// </description></item>
    /// <item><description>
    /// Integers, <see cref="bool"/>, <see cref="char"/>, <see cref="string"/>, enums — invariant
    /// culture via the <see cref="IFormattable"/> fallback (or the value itself, for
    /// <see cref="string"/>). No precision concern exists: each of these has an exact, complete
    /// default rendering. Invariant culture still matters for integers, whose sign character and
    /// digit shapes are culture-dependent in some locales.
    /// </description></item>
    /// <item><description>
    /// Anything else — <see cref="IFormattable"/> with a <see langword="null"/> format under
    /// invariant culture when the type implements it (this covers <c>Half</c>, <c>Int128</c>,
    /// <c>BigInteger</c> and any user type that honours the contract); otherwise a bare
    /// <c>ToString()</c>, which is all the CLR offers. Custom types used as ETag inputs are
    /// therefore expected to have a stable, complete, culture-independent <c>ToString()</c>;
    /// a <c>byte[]</c> row-version column remains the recommended input and never reaches here.
    /// </description></item>
    /// </list>
    /// </remarks>
    internal static string Format(object value)
    {
        return value switch
        {
            // Fast path: already text.
            string s => s,

            // Round-trip ("O") — valid only for these four.
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("O", CultureInfo.InvariantCulture),

            // Round-trip, type-specific specifier ("O" throws for both).
            TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
            Guid g => g.ToString("D", CultureInfo.InvariantCulture),

            // Everything else: invariant culture, default (shortest-round-trippable) form.
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),

            // Non-IFormattable (e.g. bool, and user types): ToString() is culture-independent by
            // construction for the BCL types that land here.
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>
    /// Appends one ETag input value to <paramref name="destination"/> in a self-delimiting,
    /// type-discriminated frame.
    /// </summary>
    /// <remarks>
    /// The framing closes three collision vectors that a plain concatenation leaves open:
    /// <list type="bullet">
    /// <item><description>
    /// <b>Separator ambiguity.</b> Every payload is length-prefixed (4-byte little-endian), so
    /// <c>("ab","c")</c> and <c>("a","bc")</c> cannot produce the same byte stream. A single-byte
    /// delimiter cannot promise this, because the delimiter can itself occur inside a value.
    /// </description></item>
    /// <item><description>
    /// <b>Null vs. empty.</b> <see langword="null"/> writes a distinct tag with no payload;
    /// <c>""</c> writes the text tag with a zero-length payload. The two never coincide, so
    /// clearing a string property changes the ETag.
    /// </description></item>
    /// <item><description>
    /// <b>Type collisions.</b> Each non-null, non-binary value carries its CLR type name, so the
    /// string <c>"1"</c> and the integer <c>1</c> hash differently even though both format to
    /// <c>"1"</c>. This matters for selectors typed as <c>object</c>, whose runtime type can vary
    /// between rows.
    /// </description></item>
    /// </list>
    /// </remarks>
    internal static void Append(Stream destination, object? value)
    {
        if (value is null)
        {
            destination.WriteByte(TagNull);
            return;
        }

        // byte[] (row-version columns) is hashed raw — no text conversion, no type name; the tag
        // alone distinguishes it from formatted values.
        if (value is byte[] bytes)
        {
            destination.WriteByte(TagBinary);
            WriteFrame(destination, bytes);
            return;
        }

        destination.WriteByte(TagText);
        WriteFrame(destination, TypeTag(value.GetType()));
        WriteFrame(destination, Encoding.UTF8.GetBytes(Format(value)));
    }

    private static byte[] TypeTag(Type type) =>
        s_typeTags.GetOrAdd(type, static t => Encoding.UTF8.GetBytes(t.FullName ?? t.Name));

    private static void WriteFrame(Stream destination, ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        destination.Write(length);
        destination.Write(payload);
    }
}
