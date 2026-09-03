using System;
using System.Threading.Tasks;

namespace OhData;

/// <summary>
/// A rejection a profile can produce: an HTTP status plus the members of the OData error envelope.
/// <para>
/// The constructor is private and the factory set is closed, so a status the framework does not
/// serve — a 2xx, a 5xx, an invented number — is unrepresentable rather than validated. #581.
/// </para>
/// <para>
/// Carries no ASP.NET Core type, so a profile that produces one still has no dependency on the
/// hosting stack; the factory translates it into the wire envelope.
/// </para>
/// </summary>
public sealed class OhDataResult
{
    private OhDataResult(int statusCode, string errorCode, string message, string? target)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Message = message;
        Target = target;
    }

    /// <summary>The HTTP status. Always a 4xx — see the type remarks.</summary>
    public int StatusCode { get; }

    /// <summary>The OData error envelope's <c>code</c>.</summary>
    public string ErrorCode { get; }

    /// <summary>The OData error envelope's <c>message</c>.</summary>
    public string Message { get; }

    /// <summary>The OData error envelope's <c>target</c>, when the rejection names one.</summary>
    public string? Target { get; }

    /// <summary>
    /// The handler succeeded and produced <paramref name="value"/>. The framework decides how to
    /// represent it — 201 vs 204 from <c>Prefer</c>, the <c>Location</c> header, the ETag — which is
    /// why there is no <c>Created</c> factory: a handler never sees <c>Prefer</c> and so cannot
    /// answer that question.
    /// </summary>
    public static OhDataResult<T> Success<T>(T? value) => OhDataResult<T>.FromValue(value);

    /// <summary>
    /// <see cref="Success{T}"/> already wrapped in a completed <see cref="Task{TResult}"/> — the
    /// shape a synchronous handler wants, and the direct replacement for
    /// <c>Task.FromResult(value)</c>.
    /// </summary>
    public static Task<OhDataResult<T>> SuccessTask<T>(T? value) =>
        Task.FromResult(OhDataResult<T>.FromValue(value));

    /// <summary>400 — the request is malformed or fails a rule the framework cannot see.</summary>
    public static OhDataResult BadRequest(string errorCode, string message, string? target = null) =>
        Create(400, errorCode, message, target);

    /// <summary>404 — the addressed resource does not exist.</summary>
    public static OhDataResult NotFound(string errorCode, string message, string? target = null) =>
        Create(404, errorCode, message, target);

    /// <summary>
    /// 403 — the caller is authenticated but not permitted. There is deliberately no
    /// <c>Unauthorized</c> factory: 401 is about authentication, which ASP.NET Core settles before
    /// a handler runs, so a handler producing one would be describing a decision it did not make.
    /// </summary>
    public static OhDataResult Forbidden(string errorCode, string message, string? target = null) =>
        Create(403, errorCode, message, target);

    /// <summary>
    /// 409 — the request is well-formed and permitted but conflicts with current state (a duplicate
    /// key, a disallowed transition). Distinct from <see cref="BadRequest"/> because the client can
    /// succeed by changing a value rather than by fixing the request, which is what retry logic
    /// needs to tell apart.
    /// </summary>
    public static OhDataResult Conflict(string errorCode, string message, string? target = null) =>
        Create(409, errorCode, message, target);

    /// <summary>
    /// 412 — a precondition failed. The framework evaluates <c>If-Match</c> before the handler
    /// (<c>CheckETagAsync</c>), but that gate is not atomic: <c>docs/etags.md</c> records the
    /// window between it and the write. A handler closing that window itself — DB-level optimistic
    /// concurrency — reports it here.
    /// </summary>
    public static OhDataResult PreconditionFailed(string errorCode, string message, string? target = null) =>
        Create(412, errorCode, message, target);

    private static OhDataResult Create(int statusCode, string errorCode, string message, string? target)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("An OData error code is required.", nameof(errorCode));
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("An OData error message is required.", nameof(message));

        return new OhDataResult(statusCode, errorCode, message, target);
    }
}

/// <summary>
/// What a handler returns: the value it produced, or a rejection (#581).
/// </summary>
/// <remarks>
/// <para>
/// Not derived from <see cref="OhDataResult"/>, and it cannot be: C# forbids a user-defined
/// conversion across an inheritance edge (CS0553), which would rule out the implicit conversion
/// that lets <c>return OhDataResult.Conflict(...)</c> compile in a handler returning this type.
/// </para>
/// <para>
/// There is deliberately no implicit conversion from <typeparamref name="T"/>: every exit states
/// its outcome. A bare <c>return model;</c> would silently mean "and whatever status the framework
/// infers", which is the kind of unexpressed meaning #496 had to unpick when a <c>null</c> was the
/// only way a handler could say "no". Being a class rather than an interface leaves that conversion
/// available later if the ceremony proves not to earn its keep.
/// </para>
/// </remarks>
public sealed class OhDataResult<T>
{
    private OhDataResult(T? value, OhDataResult? rejection)
    {
        Value = value;
        Rejection = rejection;
    }

    /// <summary>The value the handler produced. Meaningful only when <see cref="IsSuccess"/>.</summary>
    public T? Value { get; }

    /// <summary>The rejection, when the handler produced one.</summary>
    public OhDataResult? Rejection { get; }

    /// <summary><c>true</c> when the handler produced a value rather than a rejection.</summary>
    public bool IsSuccess => Rejection is null;

    internal static OhDataResult<T> FromValue(T? value) => new(value, null);

    /// <summary>
    /// Lets a handler <c>return OhDataResult.Conflict(...)</c> directly — the rejection carries no
    /// value, so there is nothing for the caller to supply.
    /// </summary>
    public static implicit operator OhDataResult<T>(OhDataResult rejection) =>
        new(default, rejection ?? throw new ArgumentNullException(nameof(rejection)));
}
