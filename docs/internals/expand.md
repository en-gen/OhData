# Batch-aware $expand

Read this when touching the batched $expand handler path (BatchHandler).

**Batch-aware `$expand` via an additive `BatchHandler`.** `HasMany`/`HasOptional`/`HasRequired` accept an optional `batchGetAll`/`batchGet` delegate (`IReadOnlyList<TKey> → ILookup<TKey,TNavigation>` or `IReadOnlyDictionary<TKey,TNavigation?>`) alongside the existing per-entity `getAll`/`get`. When present, `$expand` calls it once per expanded property per page instead of once per parent entity (collapsing N×P sequential calls to P). A per-entity `Handler` is auto-derived from the batch delegate by calling it with a single-key list, so the standalone nav-GET route, `$count`, and `$ref` keep working without a second handler. Falls back byte-identically to the per-entity path when no batch handler is registered.
