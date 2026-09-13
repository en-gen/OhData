// Deliberately in the GLOBAL namespace, and therefore in its own file: ETagValueFormatterTests
// has a file-scoped namespace, so a namespace-less type cannot live in it. Nothing else in the
// suite reaches StableTypeName's Namespace-is-null branch.

public sealed class EtagGlobalNamespaceKey
{
}
