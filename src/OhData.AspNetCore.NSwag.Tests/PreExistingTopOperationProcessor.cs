using NJsonSchema;
using NSwag;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace OhData.AspNetCore.NSwag.Tests;

/// <summary>
/// Test-only operation processor that pre-adds a <c>$top</c> query parameter (with a
/// distinctive description) to the operation for a specific path, simulating a processor
/// that ran before <see cref="OhDataNSwagOperationProcessor"/> in the pipeline. Used to prove
/// the real processor's "$top already present" duplicate guard fires against processors other
/// than itself, not just against its own prior runs.
/// </summary>
internal sealed class PreExistingTopOperationProcessor : IOperationProcessor
{
    internal const string MarkerDescription = "PRE-EXISTING-TOP";

    private readonly string _path;

    public PreExistingTopOperationProcessor(string path)
    {
        _path = path;
    }

    public bool Process(OperationProcessorContext context)
    {
        // NSwag's OperationDescription.Path (as seen by processors) still carries the raw
        // minimal-API route template's trailing slash (from OhData's MapGroup("").MapGet("")
        // pattern) — the trailing slash is only stripped later when the final "paths" object
        // is written out. Compare against the trimmed form so callers can pass the same path
        // string that shows up in the generated document.
        if (context.OperationDescription.Path.TrimEnd('/') != _path) { return true; }

        context.OperationDescription.Operation.Parameters.Add(new OpenApiParameter
        {
            Name = "$top",
            Kind = OpenApiParameterKind.Query,
            IsRequired = false,
            Schema = new JsonSchema { Type = JsonObjectType.String },
            Description = MarkerDescription,
        });

        return true;
    }
}
