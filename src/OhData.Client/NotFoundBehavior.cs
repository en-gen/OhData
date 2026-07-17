namespace OhData.Client;

/// <summary>
/// Controls how 404 Not Found responses are handled for single-entity GET operations.
/// </summary>
public enum NotFoundBehavior
{
    /// <summary>
    /// Returns <see langword="null"/> when the entity is not found.
    /// This is the default behavior.
    /// </summary>
    ReturnNull,

    /// <summary>
    /// Throws <see cref="ODataClientException"/> with status 404 when the entity is not found.
    /// </summary>
    Throw
}
