// No namespace declaration, deliberately: this type exists so ETagValueFormatterTests can exercise
// StableTypeName's Namespace-is-null branch, which nothing else in the suite reaches. A model in
// the global namespace is an ordinary shape for a small app, and without this the branch is free to
// start emitting a leading "." with no test objecting.

/// <summary>A key type in the global namespace. Used only by
/// <c>ETagValueFormatterTests.StableTypeName_OmitsTheSeparator_ForAGlobalNamespaceType</c>.</summary>
public sealed class EtagGlobalNamespaceKey
{
}
