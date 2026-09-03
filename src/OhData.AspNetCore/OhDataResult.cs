using System;

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
