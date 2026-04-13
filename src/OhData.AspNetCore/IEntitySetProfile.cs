namespace OhData.Abstractions;

/// <summary>
/// Marker interface for entity set profiles. Constrains <c>OhDataBuilder.AddProfile&lt;TProfile&gt;()</c>
/// to types that are actual profiles and carry the necessary internal interfaces.
/// </summary>
public interface IEntitySetProfile { }
