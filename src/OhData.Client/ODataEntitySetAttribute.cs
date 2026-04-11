namespace OhData.Client;

/// <summary>
/// Overrides the OData entity set name used by <see cref="OhDataClient.For{T}()"/>
/// when the conventional pluralisation of <typeparamref name="T"/>'s name is not correct.
/// </summary>
/// <example>
/// <code>
/// [ODataEntitySet("Categories")]
/// public class Category { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ODataEntitySetAttribute(string name) : Attribute
{
    /// <summary>The OData entity set name (e.g. <c>"Categories"</c>).</summary>
    public string Name { get; } = name;
}
