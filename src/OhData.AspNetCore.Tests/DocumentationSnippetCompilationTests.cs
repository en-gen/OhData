using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #622 — the documentation examples marked <c>&lt;!-- compile --&gt;</c> are compiled here, so an
/// API change that invalidates one fails CI instead of shipping.
/// <para>
/// #620 is why this exists. #581 changed all eight entity-set handler delegates to
/// <c>Task&lt;OhDataResult&lt;T&gt;&gt;</c> and every one of the 45 handler examples across 15 files
/// went stale, while the <c>docs.yml</c> stale-API gate stayed green through all of it: that gate is
/// a denylist of removed API <em>names</em>, and <c>Task.FromResult(...)</c> is valid C# naming no
/// removed API. "This snippet no longer compiles" is not a token, so no denylist can express it.
/// </para>
/// <para>
/// <b>Opt-in, deliberately.</b> Of 155 <c>csharp</c> fences in the docs, 22 contain a <c>...</c>
/// elision and most of the rest are fragments — a bare <c>ExpandProperties(x =&gt; x.Lines, ...)</c>
/// call, a single expression, one member of a class defined three pages away. Compiling all of them
/// means a per-shape harness for an 87-item tail, and a harness that half-works gives false
/// confidence, which is worse than the grep it replaces. So a snippet is compiled when it is marked,
/// the covered set is visible in the source, and it grows.
/// </para>
/// <para>
/// <b>One compilation per page; one syntax tree per fence.</b> A fence IS a file — the pages label
/// them <c>// Models.cs</c>, <c>// ProductProfile.cs</c>, <c>// Program.cs</c> — so each becomes its
/// own tree and they compile together as one assembly. That is what lets a fence carry its own
/// <c>using</c>s and <c>namespace</c>, and lets one fence hold the top-level statements C# permits in
/// only a single file. Concatenating them into one buffer instead does not parse.
/// </para>
/// </summary>
public sealed class DocumentationSnippetCompilationTests
{
    // Applied to every page's compilation. Doc snippets omit usings the way prose does; requiring
    // them inside the fences would make the examples worse to read in order to make this test easier
    // to write, which is the wrong trade. A page that needs more can declare them in its own fence.
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Reflection;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.OData.Deltas;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.Extensions.Configuration;
        using Microsoft.Extensions.DependencyInjection;
        using OhData;
        using OhData.AspNetCore.Mapper;

        """;

    private static readonly Regex FencedCSharp = new(
        @"(?<marker><!--\s*compile\s*-->)\s*\r?\n```csharp\r?\n(?<body>.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Every assembly the test host already resolved — the runtime's own, plus OhData, EF Core and
    // ASP.NET Core, which this project references. Cheaper and far more robust than naming a
    // reference set by hand, which would drift from the TFM on every upgrade.
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "src", "OhData.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // Path.Join, not Path.Combine, throughout: Combine DISCARDS everything before a rooted
    // segment, so it silently answers a different question than the one asked. Every segment here
    // is a literal or a GetRelativePath result and so cannot be rooted -- but that is an argument
    // about today's arguments, not about the call, and Join has no such rule to reason about.
    private static IEnumerable<string> DocFiles(string root) =>
        // README.md is FIRST because it is the page an adopter reads before any other, and it was
        // outside this scan until #653: the CancellationToken removal updated it, that commit missed
        // the merge window, and nothing here could tell. It is the same blind spot #620 found for the
        // docs.yml stale-API scan, which globbed docs/ and docs-site/ and so skipped the most-read
        // file in the repo.
        new[] { Path.Join(root, "README.md") }
            .Concat(Directory.EnumerateFiles(Path.Join(root, "docs"), "*.md", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Join(root, "docs-site"), "*.md", SearchOption.TopDirectoryOnly))
            // design notes and superpowers plans are historical and may reference old APIs, exactly as
            // the docs.yml stale-API scan excludes them.
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}design{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}superpowers{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);

    public static TheoryData<string> MarkedPages()
    {
        string root = RepoRoot();
        var data = new TheoryData<string>();
        foreach (string file in DocFiles(root))
        {
            if (FencedCSharp.IsMatch(File.ReadAllText(file)))
            {
                data.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MarkedPages))]
    public void EveryMarkedSnippetOnThePageCompiles(string relativePath)
    {
        string root = RepoRoot();
        string text = File.ReadAllText(Path.Join(root, relativePath));

        string[] sources = FencedCSharp.Matches(text)
            .Select(m => Usings + m.Groups["body"].Value)
            .ToArray();

        CSharpCompilation compilation = CSharpCompilation.Create(
            "DocSnippets_" + Path.GetFileNameWithoutExtension(relativePath).Replace('-', '_'),
            sources.Select(src => CSharpSyntaxTree.ParseText(src)),
            References,
            // ConsoleApplication, not a library: a page's Program.cs fence is top-level statements,
            // which CS8805 refuses in a library. Nothing is executed -- only GetDiagnostics() runs.
            // Nullable ENABLED, without which the CS86xx family below is never produced at all and
            // the check is inert -- verified by A/B: reverting a README handler to the pre-#641
            // non-nullable signature passed until this line was added.
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        // Nullability diagnostics are warnings, not errors -- but a delegate's nullability IS its
        // contract here (#641: GetById/Put/Patch are OhDataResult<TModel?> and Post is not), so a
        // page that assigns a non-nullable handler compiles "clean" while documenting the wrong
        // signature. Measured: the README quick start did exactly that after #642 and this suite
        // passed it. Treated as failures for that reason.
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                     || d.Id is "CS8619" or "CS8621" or "CS8603" or "CS8600" or "CS8604")
            .ToArray();

        Assert.True(errors.Length == 0,
            $"{relativePath}: {errors.Length} compile error(s) in the marked snippet(s).{Environment.NewLine}" +
            string.Join(Environment.NewLine, errors.Select(Describe)));
    }

    // A Roslyn location points into one synthesized tree, which is not a place a reader can look.
    // Quote the offending line instead.
    private static string Describe(Diagnostic diagnostic)
    {
        FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
        SourceText? text = diagnostic.Location.SourceTree?.GetText();
        int line = span.StartLinePosition.Line;
        string quoted = text is not null && line >= 0 && line < text.Lines.Count
            ? text.Lines[line].ToString().Trim()
            : "<unknown>";
        return $"  {diagnostic.Id}: {diagnostic.GetMessage()}{Environment.NewLine}    at: {quoted}";
    }

    [Fact]
    public void AtLeastOnePageIsCovered()
    {
        // A marker typo, a regex change, or a moved directory would otherwise turn this whole suite
        // into a silent no-op that still reports green -- the failure mode #620 is about, one layer
        // up. This is the tripwire for that.
        Assert.NotEmpty(MarkedPages());
    }
}
