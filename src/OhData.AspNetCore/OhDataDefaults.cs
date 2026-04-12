namespace OhData.AspNetCore;

/// <summary>
/// Well-known constant values used by the OhData framework.
/// </summary>
public static class OhDataDefaults
{
    /// <summary>
    /// The registration key used when <c>AddOhData()</c> is called without an explicit name.
    /// Pass this value to <c>MapOhData(name)</c> or <see cref="OhDataRegistrationCollection.Get"/>
    /// if you need to resolve the default registration programmatically.
    /// </summary>
    public const string DefaultRegistrationName = "__default__";
}
