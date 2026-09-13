using System;
using System.Collections.Generic;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Coverage for <see cref="OhDataAuthRequirementsText"/>, whose whole job is deciding what
/// authorization detail is safe to put in a generated document.
/// </summary>
/// <remarks>
/// <para>
/// A mutation sweep found this file at <b>34 mutants, none of them covered by any test</b> — the
/// largest wholly-uncovered file in the core package. That matters more than the count suggests:
/// <see cref="AuthRequirementDisclosure"/> exists to keep exact claim VALUES out of a public
/// OpenAPI document, and its own remarks say such a value "can be an internal identifier". Nothing
/// verified that <see cref="AuthRequirementDisclosure.Kinds"/> actually withholds them.
/// </para>
/// <para>
/// It is also shared deliberately: both the OpenAPI and NSwag companions render through it so the
/// two documents stay byte-identical (#220). A change here moves two packages at once, which is
/// the other reason it should not have been the least-constrained file in the assembly.
/// </para>
/// </remarks>
public class OhDataAuthRequirementsTextTests
{
    private static AuthRequirement Authenticated() => new(AuthRequirementKind.AuthenticatedUser);

    private static AuthRequirement Role(params string[] names) =>
        new(AuthRequirementKind.Role, Values: names);

    private static AuthRequirement Claim(string type, params string[] values) =>
        new(AuthRequirementKind.Claim, type, values.Length == 0 ? null : values);

    private static AuthRequirement Policy(string name) => new(AuthRequirementKind.Policy, name);

    private static string? Render(AuthRequirementDisclosure disclosure, params AuthRequirement[] reqs) =>
        OhDataAuthRequirementsText.Render(reqs, disclosure);

    // ── The disclosure boundary ───────────────────────────────────────────────────

    /// <summary>
    /// At <see cref="AuthRequirementDisclosure.Kinds"/> the claim TYPE is rendered and the required
    /// VALUES are not — the property the level exists for.
    /// </summary>
    [Fact]
    public void Kinds_RendersTheClaimType_AndWithholdsTheValues()
    {
        string? text = Render(AuthRequirementDisclosure.Kinds, Claim("tenant_id", "acme-internal-42"));

        Assert.Equal("Requires claim `tenant_id`.", text);
        Assert.DoesNotContain("acme-internal-42", text!, StringComparison.Ordinal);
    }

    /// <summary>
    /// At <see cref="AuthRequirementDisclosure.Full"/> the same input renders the values, so the
    /// test above is pinning a real difference rather than a renderer that never emits them.
    /// </summary>
    [Fact]
    public void Full_RendersTheClaimValues()
    {
        string? text = Render(AuthRequirementDisclosure.Full, Claim("tenant_id", "acme-internal-42"));

        Assert.Equal("Requires claim `tenant_id` = `acme-internal-42`.", text);
    }

    /// <summary>Multiple claim values are OR-joined, at Full only.</summary>
    [Fact]
    public void Full_OrJoinsMultipleClaimValues_AndKindsStillWithholdsThemAll()
    {
        Assert.Equal(
            "Requires claim `scope` = `read` or `write`.",
            Render(AuthRequirementDisclosure.Full, Claim("scope", "read", "write")));

        string? kinds = Render(AuthRequirementDisclosure.Kinds, Claim("scope", "read", "write"));
        Assert.Equal("Requires claim `scope`.", kinds);
        Assert.DoesNotContain("read", kinds!, StringComparison.Ordinal);
        Assert.DoesNotContain("write", kinds!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A claim requirement with no values renders identically at both levels — there is nothing to
    /// withhold, and the Full branch must not emit a dangling <c>=</c>.
    /// </summary>
    [Fact]
    public void AValuelessClaim_RendersTheSameAtBothLevels()
    {
        Assert.Equal("Requires claim `email_verified`.",
            Render(AuthRequirementDisclosure.Kinds, Claim("email_verified")));
        Assert.Equal("Requires claim `email_verified`.",
            Render(AuthRequirementDisclosure.Full, Claim("email_verified")));
    }

    // ── The non-sensitive kinds ───────────────────────────────────────────────────

    /// <summary>
    /// Role names and policy names are explicitly NOT the sensitive surface, so both levels render
    /// them. Roles are OR-within-requirement; requirements are AND-joined with "; ".
    /// </summary>
    [Theory]
    [InlineData(AuthRequirementDisclosure.Kinds)]
    [InlineData(AuthRequirementDisclosure.Full)]
    public void RolesAndPolicies_RenderAtBothLevels(AuthRequirementDisclosure disclosure)
    {
        Assert.Equal("Requires role `admin`.", Render(disclosure, Role("admin")));
        Assert.Equal("Requires role `admin` or `ops`.", Render(disclosure, Role("admin", "ops")));
        Assert.Equal("Requires policy `CanEdit`.", Render(disclosure, Policy("CanEdit")));
        Assert.Equal("Requires an authenticated user.", Render(disclosure, Authenticated()));
    }

    /// <summary>Requirements combine with AND, rendered as a "; " list inside one sentence.</summary>
    [Fact]
    public void MultipleRequirements_AreAndJoined_InDeclarationOrder() =>
        Assert.Equal(
            "Requires an authenticated user; role `admin`; policy `CanEdit`.",
            Render(AuthRequirementDisclosure.Kinds, Authenticated(), Role("admin"), Policy("CanEdit")));

    // ── Nothing statically documentable ───────────────────────────────────────────

    /// <summary>
    /// Null, empty, and resource-only inputs render <c>null</c> — not an empty "Requires ." sentence.
    /// A resource (Layer B) requirement is evaluated against the loaded entity and cannot be
    /// described statically, so it contributes nothing.
    /// </summary>
    [Fact]
    public void NothingDocumentable_RendersNull()
    {
        Assert.Null(OhDataAuthRequirementsText.Render(null, AuthRequirementDisclosure.Full));
        Assert.Null(OhDataAuthRequirementsText.Render(Array.Empty<AuthRequirement>(), AuthRequirementDisclosure.Full));
        Assert.Null(Render(AuthRequirementDisclosure.Full, new AuthRequirement(AuthRequirementKind.Resource, "P")));
    }

    /// <summary>
    /// A resource requirement beside a documentable one is skipped rather than dropping the whole
    /// sentence.
    /// </summary>
    [Fact]
    public void AResourceRequirement_IsSkipped_ButItsSiblingsStillRender() =>
        Assert.Equal(
            "Requires role `admin`.",
            Render(AuthRequirementDisclosure.Kinds,
                new AuthRequirement(AuthRequirementKind.Resource, "P"), Role("admin")));

    /// <summary>A role requirement carrying no names has nothing to render and is skipped.</summary>
    [Fact]
    public void ARoleRequirementWithNoNames_IsSkipped() =>
        Assert.Null(Render(AuthRequirementDisclosure.Kinds, new AuthRequirement(AuthRequirementKind.Role)));

    // ── AppendSection: idempotent by contract ─────────────────────────────────────

    /// <summary>
    /// The section is appended under <see cref="OhDataAuthRequirementsText.SectionLabel"/>, and
    /// appending the SAME section twice is a no-op — both companions may register their filter, and
    /// a double-append would ship a duplicated paragraph in the document.
    /// </summary>
    [Fact]
    public void AppendSection_IsIdempotent_ForAnAlreadyPresentSection()
    {
        string once = OhDataAuthRequirementsText.AppendSection("Gets a widget.", "Requires role `admin`.");

        Assert.Equal("Gets a widget.\n\n**Authorization:** Requires role `admin`.", once);
        Assert.Equal(once, OhDataAuthRequirementsText.AppendSection(once, "Requires role `admin`."));
    }

    /// <summary>A different requirements sentence IS appended, so idempotence is not "never append".</summary>
    [Fact]
    public void AppendSection_StillAppends_ADifferentSection()
    {
        string once = OhDataAuthRequirementsText.AppendSection("Gets a widget.", "Requires role `admin`.");
        string twice = OhDataAuthRequirementsText.AppendSection(once, "Requires policy `CanEdit`.");

        Assert.NotEqual(once, twice);
        Assert.Contains("Requires role `admin`.", twice, StringComparison.Ordinal);
        Assert.Contains("Requires policy `CanEdit`.", twice, StringComparison.Ordinal);
    }

    /// <summary>With no existing description the section stands alone, with no leading blank lines.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AppendSection_WithNoExistingDescription_ReturnsTheSectionAlone(string? existing) =>
        Assert.Equal("**Authorization:** Requires role `admin`.",
            OhDataAuthRequirementsText.AppendSection(existing, "Requires role `admin`."));
}
