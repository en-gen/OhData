using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── Fixtures ─────────────────────────────────────────────────────────────────
//
// The base class is there for the INHERITED property: typeof(TModel).GetProperties() reports
// ReflectedType = PcCustomer for it, while System.Text.Json's JsonPropertyInfo.AttributeProvider
// reports the declaring base — so PropertyInfo equality (== / Equals) is FALSE for the same member
// and only HasSameMetadataDefinitionAs pairs them (#462's third defect, measured on .NET 10.0.11).

public class PcPersonBase
{
    public int Id { get; set; }

    /// <summary>Inherited and multi-word: reaches the table only through the contract loop.</summary>
    public string? AuditNote { get; set; }
}

public class PcCustomer : PcPersonBase
{
    /// <summary>
    /// Multi-word on purpose. A single-word property's snake_case spelling differs from its CLR
    /// name only by case, which the table's <c>OrdinalIgnoreCase</c> comparer already covers — that
    /// is exactly why camelCase hid this defect and <c>SnakeCaseLower</c> did not.
    /// </summary>
    public string? FirstName { get; set; }

    /// <summary>Withheld from the published contract, so it must NOT gain a contract key.</summary>
    public string? InternalNote { get; set; }
}

public sealed class PcCustomerProfile : EntitySetProfile<int, PcCustomer>
{
    public static string[]? LastChangedProperties;
    public static PcCustomer? LastPatched;

    public PcCustomerProfile() : base(x => x.Id)
    {
        EntitySetName = "PcCustomers";
        Ignore(x => x.InternalNote!);

        GetById = (id, _) => OhDataResult.Success<PcCustomer>(new PcCustomer { Id = id });

        Patch = (id, delta, _) =>
        {
            LastChangedProperties = delta.GetChangedPropertyNames().ToArray();
            var row = new PcCustomer { Id = id };
            delta.Patch(row);
            LastPatched = row;
            return OhDataResult.Success<PcCustomer>(row);
        };
    }
}

/// <summary>
/// A multi-word KEY, so #454's key-immutability guard is reached through the new contract key
/// rather than through the case-insensitive CLR alias: <c>Id</c> and <c>id</c> collapse under
/// <c>OrdinalIgnoreCase</c>, <c>OrderId</c> and <c>order_id</c> do not.
/// </summary>
public class PcOrder
{
    public int OrderId { get; set; }
    public string? Customer { get; set; }
}

public sealed class PcOrderProfile : EntitySetProfile<int, PcOrder>
{
    public static string[]? LastChangedProperties;

    public PcOrderProfile() : base(x => x.OrderId)
    {
        EntitySetName = "PcOrders";

        GetById = (id, _) => OhDataResult.Success<PcOrder>(new PcOrder { OrderId = id });

        Patch = (id, delta, _) =>
        {
            LastChangedProperties = delta.GetChangedPropertyNames().ToArray();
            var row = new PcOrder { OrderId = id };
            delta.Patch(row);
            return OhDataResult.Success<PcOrder>(row);
        };
    }
}

/// <summary>
/// #536 — <c>PATCH</c>'s body-name table resolved through the EDM name (deliberately policy-free,
/// OData §4.4) plus the CLR name, while the value it binds is deserialized with the registration's
/// serializer options. Under a non-case-preserving <see cref="JsonNamingPolicy"/> the two disagree,
/// and PATCH silently dropped every body key the binder would have matched:
/// <c>PATCH /Customers(1) {"first_name":"x"}</c> answered <c>200</c> and changed nothing.
/// <para>
/// This is #511 manifestation (2) surviving on one route. The fix is #511's: the table's primary
/// key is read off the contract the binder resolves (<c>JsonTypeInfo.Properties[].Name</c>), with
/// the EDM and CLR names demoted to non-overwriting aliases.
/// </para>
/// </summary>
public class Issue536PatchBinderContractTests
{
    private static HttpContent Json(string raw) =>
        new StringContent(raw, Encoding.UTF8, "application/json");

    private static JsonNamingPolicy PolicyFor(string bodyName) =>
        bodyName.Contains('_') ? JsonNamingPolicy.SnakeCaseLower : JsonNamingPolicy.KebabCaseLower;

    // ── The defect ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("first_name")]
    [InlineData("first-name")]
    public async Task Patch_NonCasePreservingNamingPolicy_BindsTheNameTheBinderWouldMatch(string bodyName)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .WithJsonPropertyNamingPolicy(PolicyFor(bodyName))
            .AddEntitySetProfile<PcCustomerProfile>());

        PcCustomerProfile.LastChangedProperties = null;
        PcCustomerProfile.LastPatched = null;

        var resp = await fx.Client.PatchAsync("/odata/PcCustomers(1)",
            Json($"{{\"{bodyName}\":\"Ada\"}}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // PRE-FIX: LastChangedProperties was empty and LastPatched.FirstName was null — a 200 that
        // discarded the write.
        Assert.Equal(new[] { "FirstName" }, PcCustomerProfile.LastChangedProperties);
        Assert.Equal("Ada", PcCustomerProfile.LastPatched!.FirstName);
    }

    /// <summary>
    /// The inherited half, and the reason the pairing must be <c>HasSameMetadataDefinitionAs</c>:
    /// with <c>==</c> the contract loop never matches an inherited member and the property silently
    /// loses its contract key — which looks exactly like the unfixed defect, on one property.
    /// </summary>
    [Theory]
    [InlineData("audit_note")]
    [InlineData("audit-note")]
    public async Task Patch_NonCasePreservingNamingPolicy_BindsAnInheritedProperty(string bodyName)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .WithJsonPropertyNamingPolicy(PolicyFor(bodyName))
            .AddEntitySetProfile<PcCustomerProfile>());

        PcCustomerProfile.LastChangedProperties = null;
        PcCustomerProfile.LastPatched = null;

        var resp = await fx.Client.PatchAsync("/odata/PcCustomers(1)",
            Json($"{{\"{bodyName}\":\"seen\"}}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(new[] { "AuditNote" }, PcCustomerProfile.LastChangedProperties);
        Assert.Equal("seen", PcCustomerProfile.LastPatched!.AuditNote);
    }

    /// <summary>
    /// #454's key guard resolves through the same table, so re-keying it moves the guard too: a
    /// multi-word key named in its policy spelling is now SEEN. Pre-fix <c>order_id</c> resolved to
    /// nothing, so the occurrence was neither validated nor applied — a silent drop where the
    /// documented answer is a 400.
    /// </summary>
    [Theory]
    [InlineData("order_id")]
    [InlineData("order-id")]
    public async Task Patch_NonCasePreservingNamingPolicy_KeyOccurrenceIsValidated(string bodyName)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .WithJsonPropertyNamingPolicy(PolicyFor(bodyName))
            .AddEntitySetProfile<PcOrderProfile>());

        var mismatch = await fx.Client.PatchAsync("/odata/PcOrders(1)",
            Json($"{{\"{bodyName}\":999,\"customer\":\"Zed\"}}"));

        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        var json = await mismatch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("key", json.GetProperty("error").GetProperty("target").GetString());

        // Matching restatement: accepted, and the key still never enters the delta.
        PcOrderProfile.LastChangedProperties = null;
        var match = await fx.Client.PatchAsync("/odata/PcOrders(1)",
            Json($"{{\"{bodyName}\":1,\"customer\":\"Zed\"}}"));

        Assert.Equal(HttpStatusCode.OK, match.StatusCode);
        Assert.Equal(new[] { "Customer" }, PcOrderProfile.LastChangedProperties);
    }

    // ── Bounding ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The EDM and CLR names stay as non-overwriting ALIASES. Dropping them would trade a per-host
    /// divergence for a per-verb one, since <c>FindClrPropertyByEdmName</c> is what the rest of the
    /// framework resolves through.
    /// </summary>
    [Fact]
    public async Task Patch_NonCasePreservingNamingPolicy_StillAcceptsTheClrAndEdmSpelling()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .WithJsonPropertyNamingPolicy(JsonNamingPolicy.SnakeCaseLower)
            .AddEntitySetProfile<PcCustomerProfile>());

        PcCustomerProfile.LastPatched = null;
        var resp = await fx.Client.PatchAsync("/odata/PcCustomers(1)",
            Json("{\"FirstName\":\"Grace\"}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("Grace", PcCustomerProfile.LastPatched!.FirstName);
    }

    /// <summary>
    /// An <c>Ignore()</c>d property is REMOVED from the contract, so it gains no contract key and
    /// the widening cannot make it newly bindable under any spelling.
    /// </summary>
    [Theory]
    [InlineData("internal_note")]
    [InlineData("InternalNote")]
    public async Task Patch_NonCasePreservingNamingPolicy_IgnoredPropertyStaysUnbindable(string bodyName)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .WithJsonPropertyNamingPolicy(JsonNamingPolicy.SnakeCaseLower)
            .AddEntitySetProfile<PcCustomerProfile>());

        PcCustomerProfile.LastChangedProperties = null;
        PcCustomerProfile.LastPatched = null;

        var resp = await fx.Client.PatchAsync("/odata/PcCustomers(1)",
            Json($"{{\"{bodyName}\":\"leak\"}}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(PcCustomerProfile.LastChangedProperties!);
        Assert.Null(PcCustomerProfile.LastPatched!.InternalNote);
    }

    /// <summary>
    /// NO-MOVEMENT CONTROL. On a default host every alias collapses onto the contract key under the
    /// comparer, so nothing shipped moves — CLR, camelCase and case-variant spellings all behave
    /// exactly as before.
    /// </summary>
    [Theory]
    [InlineData("FirstName")]
    [InlineData("firstName")]
    [InlineData("firstname")]
    public async Task Patch_DefaultHost_UnchangedByTheReKey(string bodyName)
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<PcCustomerProfile>());

        PcCustomerProfile.LastPatched = null;
        var resp = await fx.Client.PatchAsync("/odata/PcCustomers(1)",
            Json($"{{\"{bodyName}\":\"Edsger\"}}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("Edsger", PcCustomerProfile.LastPatched!.FirstName);
    }

    [Fact]
    public async Task Patch_DefaultHost_InheritedPropertyAndUnknownKeyUnchanged()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<PcCustomerProfile>());

        PcCustomerProfile.LastChangedProperties = null;
        PcCustomerProfile.LastPatched = null;

        var resp = await fx.Client.PatchAsync("/odata/PcCustomers(1)",
            Json("{\"auditNote\":\"kept\",\"nosuchmember\":1}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(new[] { "AuditNote" }, PcCustomerProfile.LastChangedProperties);
        Assert.Equal("kept", PcCustomerProfile.LastPatched!.AuditNote);
    }
}
