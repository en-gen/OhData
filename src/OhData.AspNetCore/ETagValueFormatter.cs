using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    /// Integers, <see cref="char"/> and enums — invariant culture via the
    /// <see cref="IFormattable"/> fallback (all three implement it). No precision concern exists:
    /// each has an exact, complete default rendering. Invariant culture still matters for
    /// integers, whose sign character is culture-dependent in some locales (<c>sv-SE</c> uses
    /// U+2212, not U+002D).
    /// </description></item>
    /// <item><description>
    /// <see cref="string"/> and <see cref="bool"/> — returned as-is, and via the bare
    /// <c>ToString()</c> line respectively. Neither implements <see cref="IFormattable"/>, so
    /// neither takes the invariant-culture branch; it makes no difference, because
    /// <see cref="bool.ToString()"/> ignores any format provider and always yields
    /// <c>"True"</c>/<c>"False"</c>.
    /// </description></item>
    /// <item><description>
    /// Anything else — <see cref="IFormattable"/> with a <see langword="null"/> format under
    /// invariant culture when the type implements it (this covers <c>Half</c>, <c>Int128</c>,
    /// <c>BigInteger</c> and any user type that honours the contract); otherwise a bare
    /// <c>ToString()</c>. A type that reaches that last line without overriding
    /// <c>ToString()</c> would format to its own type name — a constant for every row — which is
    /// why <see cref="IsSupportedSelectorType"/> rejects such selectors at startup rather than
    /// letting the collision ship.
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
            DateTime dt => FormatDateTime(dt),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("O", CultureInfo.InvariantCulture),

            // Round-trip, type-specific specifier ("O" throws for both).
            TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
            Guid g => g.ToString("D", CultureInfo.InvariantCulture),

            // Everything else: invariant culture, default (shortest-round-trippable) form.
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),

            // Non-IFormattable. `bool` and `string` are the only types that reach here under a
            // selector that passed IsSupportedSelectorType, and both have an invariant ToString().
            // Anything else arriving here is a type the startup validation could not see through
            // (a selector statically typed as an allowed base/interface); it stays lenient rather
            // than throwing — see the remarks on IsSupportedSelectorType.
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>
    /// <see cref="DateTimeKind.Local"/> is formatted with the <see cref="DateTimeKind.Unspecified"/>
    /// pattern plus an explicit marker, NOT with <c>"O"</c>'s local-offset suffix.
    /// </summary>
    /// <remarks>
    /// <c>"O"</c> renders a <c>Local</c> value as <c>…-06:00</c>, appending
    /// <c>TimeZoneInfo.Local.GetUtcOffset(value)</c> — i.e. the *server's timezone configuration*
    /// would become part of the hash. That breaks the invariance this type exists to provide, in
    /// two ways: a client that reads from a <c>TZ=UTC</c> node and writes to a
    /// <c>TZ=America/Chicago</c> node behind the same load balancer gets `412` forever; and a
    /// tzdata update that changes the DST rules for a *future* local timestamp rotates every
    /// outstanding ETag on a single node with no deployment. `.ToUniversalTime()` is not a fix
    /// either — it shifts the ticks by the same machine-dependent offset, relocating the problem
    /// rather than removing it. Emitting the raw wall-clock reading plus a Kind marker keeps the
    /// value machine-independent, lossless, and still distinct from the same ticks stored as
    /// <c>Utc</c> or <c>Unspecified</c>.
    /// </remarks>
    private static string FormatDateTime(DateTime value) => value.Kind switch
    {
        // "O" already discriminates these two machine-independently: Utc ends in 'Z',
        // Unspecified has no suffix at all.
        DateTimeKind.Utc or DateTimeKind.Unspecified => value.ToString("O", CultureInfo.InvariantCulture),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Unspecified).ToString("O", CultureInfo.InvariantCulture)
             + LocalKindMarker,
    };

    /// <summary>Suffix that keeps a <see cref="DateTimeKind.Local"/> value distinct from the same
    /// ticks stored as <see cref="DateTimeKind.Unspecified"/>, without embedding an offset.</summary>
    private const string LocalKindMarker = "[Local]";

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

        // Row-version buffers are hashed raw — no text conversion, no type name; the tag alone
        // distinguishes them from formatted values. Every buffer shape a row-version column
        // realistically arrives as is handled here: `byte[]` plus the four BCL wrappers, all of
        // which are non-IFormattable and would otherwise format to their *type name* — a value
        // identical for every row (see IsSupportedSelectorType).
        switch (value)
        {
            case byte[] bytes:
                WriteBinary(destination, bytes);
                return;
            case ImmutableArray<byte> immutable:
                // A `default` ImmutableArray<byte> is the "no value" state, not an empty buffer.
                if (immutable.IsDefault) { destination.WriteByte(TagNull); return; }
                WriteBinary(destination, immutable.AsSpan());
                return;
            case ReadOnlyMemory<byte> readOnlyMemory:
                WriteBinary(destination, readOnlyMemory.Span);
                return;
            case Memory<byte> memory:
                WriteBinary(destination, memory.Span);
                return;
            case ArraySegment<byte> segment:
                WriteBinary(destination, segment.AsSpan());
                return;
        }

        destination.WriteByte(TagText);
        WriteFrame(destination, TypeTag(value.GetType()));
        WriteFrame(destination, Encoding.UTF8.GetBytes(Format(value)));
    }

    // All binary shapes share one tag and carry no type name: they are the same row-version bytes
    // however the model happens to wrap them, and a selector's static type is fixed per profile,
    // so no cross-shape collision is reachable.
    private static void WriteBinary(Stream destination, ReadOnlySpan<byte> payload)
    {
        destination.WriteByte(TagBinary);
        WriteFrame(destination, payload);
    }

    private static byte[] TypeTag(Type type) =>
        s_typeTags.GetOrAdd(type, static t => Encoding.UTF8.GetBytes(StableTypeName(t)));

    /// <summary>
    /// A version-independent name for <paramref name="type"/>, used as the hash's type
    /// discriminator.
    /// </summary>
    /// <remarks>
    /// <see cref="Type.FullName"/> cannot be used: for a *constructed generic* type it embeds each
    /// argument's assembly identity, including its version —
    /// <c>ValueTuple`2[[System.Decimal, System.Private.CoreLib, Version=8.0.0.0, …]]</c> on
    /// net8.0 versus <c>Version=10.0.0.0</c> on net10.0. That would mint different ETags for
    /// identical data across the two builds of this package (silently invalidating every ETag on a
    /// TFM migration, with no source change to point at), and for a user's own generic type it
    /// would fold in the *application's* assembly version, rotating every ETag on every version
    /// bump. Recursing through the generic arguments by name keeps the full discriminating power
    /// with none of the version coupling. A type *rename* still changes the ETag — that is a
    /// source change under the author's control, and it is documented in docs/etags.md.
    /// </remarks>
    internal static string StableTypeName(Type type)
    {
        if (type.IsArray)
        {
            return StableTypeName(type.GetElementType()!) + "[]";
        }

        if (type.IsConstructedGenericType)
        {
            Type[] arguments = type.GetGenericArguments();
            string[] parts = new string[arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                parts[i] = StableTypeName(arguments[i]);
            }

            return StableTypeName(type.GetGenericTypeDefinition()) + "[" + string.Join(",", parts) + "]";
        }

        // Type.Name omits declaring types, so walk the nesting chain to keep Outer+Inner distinct
        // from another Outer's Inner. Never touches assembly identity. Collected outward-in and
        // reversed rather than prepended in the loop: prepending reallocates the whole string each
        // time, which is quadratic in nesting depth.
        var segments = new List<string> { type.Name };
        for (Type? declaring = type.DeclaringType; declaring is not null; declaring = declaring.DeclaringType)
        {
            segments.Add(declaring.Name);
        }

        segments.Reverse();
        string name = string.Join("+", segments);

        return type.Namespace is null ? name : type.Namespace + "." + name;
    }

    /// <summary>
    /// Whether a <c>UseETag</c> selector returning <paramref name="type"/> can be hashed into a
    /// sound ETag. Enforced once at <c>MapOhData()</c>; see the caller in
    /// <c>OhDataEndpointFactory.MapEntitySet</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure this rejects is silent and total. A type that neither implements
    /// <see cref="IFormattable"/> nor overrides <c>ToString()</c> formats to its own *type name* —
    /// the same string for every row — so every entity in the set shares one ETag and `If-Match`
    /// degrades to a no-op. <c>List&lt;string&gt;</c> (an ETag over a navigation property) and a
    /// plain POCO both land there. A type that *does* override <c>ToString()</c> can be just as
    /// wrong in a subtler way: <see cref="TimeZoneInfo"/> returns its <c>DisplayName</c>, which is
    /// UI-culture dependent — reintroducing, through the fallback, the very culture bug this type
    /// exists to fix.
    /// </para>
    /// <para>
    /// Because the consequence is a concurrency primitive that silently does nothing, a loud
    /// startup exception is the right trade against the small chance of a false positive; the
    /// remedy is always to select a scalar projection instead (<c>x =&gt; x.Thing.Id</c>).
    /// The check is deliberately made at startup and NOT at hash time: <c>InvokeGetETag</c> runs on
    /// the response path, after a handler has already run and very possibly persisted, so throwing
    /// there would turn a formatting surprise into a `500` on an operation that already succeeded.
    /// It also cannot see runtime types, only declared ones — a selector declared as
    /// <see cref="IFormattable"/> is accepted here and its runtime value stays on the lenient
    /// fallback path.
    /// </para>
    /// </remarks>
    internal static bool IsSupportedSelectorType(Type type)
    {
        Type target = Nullable.GetUnderlyingType(type) ?? type;

        if (target.IsEnum)
        {
            return true;
        }

        if (s_supportedSelectorTypes.Contains(target))
        {
            return true;
        }

        // Any type that honours IFormattable has an invariant-culture rendering by contract
        // (BigInteger, Int128, Half, and user-defined value types included).
        return typeof(IFormattable).IsAssignableFrom(target);
    }

    private static readonly HashSet<Type> s_supportedSelectorTypes = new()
    {
        // Binary row-version buffers (hashed raw).
        typeof(byte[]),
        typeof(ImmutableArray<byte>),
        typeof(ReadOnlyMemory<byte>),
        typeof(Memory<byte>),
        typeof(ArraySegment<byte>),

        // Text and the two non-IFormattable primitives whose ToString() is invariant anyway.
        typeof(string),
        typeof(bool),
    };

    private static void WriteFrame(Stream destination, ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        destination.Write(length);
        destination.Write(payload);
    }
}
