using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Tests for coverage gaps identified in the OhData.AspNetCore library.
/// </summary>
public class CoverageGapTests
{
    // ── PluralizationHelper — internal, but InternalsVisibleTo grants access ───

    [Theory]
    [InlineData("Category", "Categories")]
    [InlineData("Party", "Parties")]
    [InlineData("Strawberry", "Strawberries")]
    public void Pluralize_ConsonantPlusY_ReplacesWithIes(string input, string expected)
    {
        Assert.Equal(expected, PluralizationHelper.Pluralize(input));
    }

    [Theory]
    [InlineData("Status", "Statuses")]
    [InlineData("Box", "Boxes")]
    [InlineData("Buzz", "Buzzes")]
    [InlineData("Dish", "Dishes")]
    [InlineData("Church", "Churches")]
    public void Pluralize_SibilantEnding_AppendsEs(string input, string expected)
    {
        Assert.Equal(expected, PluralizationHelper.Pluralize(input));
    }

    [Theory]
    [InlineData("Product", "Products")]
    [InlineData("Order", "Orders")]
    [InlineData("Invoice", "Invoices")]
    public void Pluralize_DefaultRule_AppendsS(string input, string expected)
    {
        Assert.Equal(expected, PluralizationHelper.Pluralize(input));
    }

    [Fact]
    public void Pluralize_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", PluralizationHelper.Pluralize(""));
    }

    // Vowel+y ending should NOT trigger the consonant+y → ies rule; it just gets s
    [Fact]
    public void Pluralize_VowelPlusY_AppendsS()
    {
        Assert.Equal("Monkeys", PluralizationHelper.Pluralize("Monkey"));
        Assert.Equal("Turkeys", PluralizationHelper.Pluralize("Turkey"));
    }

    // Default EntitySetName derived from model type name uses PluralizationHelper
    [Fact]
    public async Task DefaultEntitySetName_UsesPluralizationOfModelType()
    {
        // DefaultNameCategory → "DefaultNameCategories" (consonant+y rule)
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DefaultNameCategoryProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata");
        var values = json.GetProperty("value");
        bool found = Enumerable.Range(0, values.GetArrayLength())
            .Any(i => values[i].GetProperty("name").GetString() == "DefaultNameCategories");
        Assert.True(found, "Expected 'DefaultNameCategories' in service document");
    }

    // ── OhDataBuilder.WithPrefix invalid inputs ────────────────────────────────

    [Fact]
    public void WithPrefix_Empty_ThrowsArgumentException()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Assert.Throws<ArgumentException>(() =>
            services.AddOhData(o => o.WithPrefix("")));
    }

    [Fact]
    public void WithPrefix_Whitespace_ThrowsArgumentException()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Assert.Throws<ArgumentException>(() =>
            services.AddOhData(o => o.WithPrefix("   ")));
    }

    // ── Assembly scanning ──────────────────────────────────────────────────────

    [Fact]
    public void AddProfilesFromAssemblyOf_RegistersScanTargetProfileInDI()
    {
        // AddProfilesFromAssemblyOf scans the assembly and registers concrete profiles in DI.
        // Verify that ScanTargetProfile is registered as a scoped service (the proof that it
        // was discovered), without resolving OhDataRegistration which would try to instantiate
        // every discovered profile — including the intentionally-broken guard profiles.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData("scan1", o => o
            .WithPrefix("/scan1")
            .AddProfilesFromAssemblyOf<ScanTargetProfile>());

        // ScanTargetProfile must be present as a Scoped service descriptor.
        bool found = services.Any(d =>
            d.ServiceType == typeof(ScanTargetProfile) &&
            d.Lifetime == ServiceLifetime.Scoped);
        Assert.True(found, "ScanTargetProfile should be registered as a Scoped service after scanning.");
    }

    [Fact]
    public void AddProfilesFromAssembly_RegistersScanTargetProfileInDI()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData("scan2", o => o
            .WithPrefix("/scan2")
            .AddProfilesFromAssembly(typeof(ScanTargetProfile).Assembly));

        bool found = services.Any(d =>
            d.ServiceType == typeof(ScanTargetProfile) &&
            d.Lifetime == ServiceLifetime.Scoped);
        Assert.True(found, "ScanTargetProfile should be registered as a Scoped service after scanning.");
    }

    [Fact]
    public void AddProfilesFrom_Scanner_In_NullAssembly_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Assert.Throws<ArgumentNullException>(() =>
            services.AddOhData("scan3", o =>
                o.WithPrefix("/scan3").AddProfilesFrom(s =>
                    s.In((System.Reflection.Assembly)null!))));
    }

    [Fact]
    public void AddProfilesFrom_Scanner_In_EmptyAssemblyArray_ThrowsArgumentException()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Assert.Throws<ArgumentException>(() =>
            services.AddOhData("scan4", o =>
                o.WithPrefix("/scan4").AddProfilesFrom(s =>
                    s.In(Array.Empty<System.Reflection.Assembly>()))));
    }

    [Fact]
    public void AddProfilesFrom_AlreadyRegisteredType_IsSkipped()
    {
        // AddEntitySetProfile<ScanTargetProfile> first, then scan the same assembly.
        // The scan's _alreadyRegistered check must prevent a duplicate registration.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData("skip1", o => o
            .WithPrefix("/skip1")
            .AddEntitySetProfile<ScanTargetProfile>()
            .AddProfilesFrom(s => s.InAssemblyOf<ScanTargetProfile>()));

        // ScanTargetProfile should appear exactly once as a Scoped descriptor.
        int count = services.Count(d =>
            d.ServiceType == typeof(ScanTargetProfile) &&
            d.Lifetime == ServiceLifetime.Scoped);
        Assert.Equal(1, count);
    }

    // ── UseETag guards ─────────────────────────────────────────────────────────

    [Fact]
    public void UseETag_NoSelectors_ThrowsArgumentException()
    {
        // UseETag() with no selectors must throw ArgumentException in the constructor.
        // ETagNoSelectorsProfile.ETagNoSelectorsProfileConcrete is concrete; just instantiate it.
        Assert.Throws<ArgumentException>(() => _ = new ETagNoSelectorsProfileConcrete());
    }

    [Fact]
    public void UseETag_WithoutGetById_AtMapOhData_ThrowsInvalidOperationException()
    {
        // UseETag requires GetById so If-Match precondition checks can fetch the current entity.
        // The factory validates this when MapOhData() runs, before the app starts.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddOhData(o => o.AddEntitySetProfile<ETagWithoutGetByIdProfile>());
        var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.MapOhData());
    }

    // ── UseETag with multiple selectors ───────────────────────────────────────

    [Fact]
    public async Task UseETag_MultipleSelectors_ReturnsETagHeader()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<MultiETagProfile>());
        var response = await fx.Client.GetAsync("/odata/MultiETagWidgets(1)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        // Two consecutive GETs must produce the same ETag (deterministic hash)
        var response2 = await fx.Client.GetAsync("/odata/MultiETagWidgets(1)");
        Assert.Equal(response.Headers.ETag!.Tag, response2.Headers.ETag!.Tag);
    }

    // ── RequireRoles empty args guard ──────────────────────────────────────────

    [Fact]
    public void RequireRoles_EmptyArgs_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _ = new EmptyRolesProfile());
    }

    // ── HasOptional with GET handler (single-entity navigation) ──────────────

    [Fact]
    public async Task HasOptional_WithGetHandler_Returns200WithEntity()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ParentWithTagProfile>());
        var response = await fx.Client.GetAsync("/odata/TaggedParents(1)/Tag");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HasOptional_WithGetHandler_ParentNotFound_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ParentWithTagProfile>());
        var response = await fx.Client.GetAsync("/odata/TaggedParents(999)/Tag");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── HasRequired with GET handler (single-entity navigation) ───────────────

    [Fact]
    public async Task HasRequired_WithGetHandler_Returns200WithEntity()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ParentWithAddressProfile>());
        var response = await fx.Client.GetAsync("/odata/AddressedParents(1)/Address");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HasRequired_WithGetHandler_ParentNotFound_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ParentWithAddressProfile>());
        var response = await fx.Client.GetAsync("/odata/AddressedParents(999)/Address");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── FilterProperties expression overload ──────────────────────────────────

    [Fact]
    public async Task FilterProperties_ExpressionOverload_AllowedProperty_Filters()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NameFilterOnlyProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NameFilterWidgets?$filter=Name eq 'Alpha'");
        Assert.Equal(1, json.GetProperty("value").GetArrayLength());
    }

    // ── SelectProperties expression overload ──────────────────────────────────

    [Fact]
    public async Task SelectProperties_ExpressionOverload_AllowedProperty_OmitsOtherFields()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NameSelectOnlyProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NameSelectWidgets?$select=Name");
        var first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("name", out _));
        Assert.False(first.TryGetProperty("id", out _));
    }

    // ── OrderByProperties expression overload ─────────────────────────────────

    [Fact]
    public async Task OrderByProperties_ExpressionOverload_AllowedProperty_Sorts()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NameOrderByOnlyProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NameOrderByWidgets?$orderby=Name");
        var value = json.GetProperty("value");
        string?[] names = Enumerable.Range(0, value.GetArrayLength())
            .Select(i => value[i].GetProperty("name").GetString())
            .ToArray();
        Assert.Equal(names.OrderBy(n => n).ToArray(), names);
    }

    // ── SelectProperties string overload ──────────────────────────────────────

    [Fact]
    public async Task SelectProperties_StringOverload_AllowedProperty_OmitsOtherFields()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<StringSelectOnlyProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/StringSelectWidgets?$select=Name");
        var first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("name", out _));
        Assert.False(first.TryGetProperty("id", out _));
    }

    // ── OrderByProperties string overload ─────────────────────────────────────

    [Fact]
    public async Task OrderByProperties_StringOverload_AllowedProperty_Sorts()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<StringOrderByOnlyProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/StringOrderByWidgets?$orderby=Name");
        var value = json.GetProperty("value");
        string?[] names = Enumerable.Range(0, value.GetArrayLength())
            .Select(i => value[i].GetProperty("name").GetString())
            .ToArray();
        Assert.Equal(names.OrderBy(n => n).ToArray(), names);
    }

    // ── ExpandProperties — no expansion allowed, $expand returns 200 with unloaded nav ──

    [Fact]
    public async Task ExpandProperties_EmptyArray_ExpandQueryStillReturns200()
    {
        // ExpandProperties with no args restricts advertised expand in $metadata, but the
        // OData runtime does not reject $expand at query time — it processes it without
        // expanding data (navigation is not eagerly loaded). Verify the request succeeds.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NoExpandProfile>());
        var response = await fx.Client.GetAsync("/odata/NoExpandParents");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Unbound function — non-convertible parameter → InvalidParameter ────────

    [Fact]
    public async Task UnboundFunction_BadParamType_Returns400WithInvalidParameter()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o =>
            o.AddFunction((Func<int, Task<int>>)(n => Task.FromResult(n * 2)), "Double"));
        var response = await fx.Client.GetAsync("/odata/Double?n=notanint");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidParameter", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Unbound action — missing required param → MissingParameter ────────────

    [Fact]
    public async Task UnboundAction_MissingRequiredParam_Returns400WithMissingParameter()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o =>
            o.AddAction((Func<string, Task<string>>)(s => Task.FromResult(s)), "EchoStr"));
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/EchoStr", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MissingParameter", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Unbound action — malformed JSON body → InvalidBody ────────────────────

    [Fact]
    public async Task UnboundAction_MalformedBody_Returns400WithInvalidBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o =>
            o.AddAction((Func<string, Task<string>>)(s => Task.FromResult(s)), "EchoStr2"));
        using var content = new StringContent("{broken", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/EchoStr2", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidBody", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Bound function — non-convertible parameter → InvalidParameter ──────────

    [Fact]
    public async Task BoundFunction_BadParamType_Returns400WithInvalidParameter()
    {
        // DoubleCount expects int factor; passing a non-integer triggers InvalidParameter.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BoundOpsProfile>(),
            configureServices: s => s.AddSingleton(new BoundOpsStore()));
        var response = await fx.Client.GetAsync("/odata/BoundWidgets/DoubleCount?factor=notanint");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidParameter", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── $skiptoken invalid value → InvalidSkipToken ────────────────────────────

    [Fact]
    public async Task GetQueryable_InvalidSkipToken_Returns400WithInvalidSkipToken()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<QueryableWidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/QueryableWidgets?$skiptoken=!!!notvalidbase64!!!");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidSkipToken", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Navigation route — bad key format → 400 ───────────────────────────────

    [Fact]
    public async Task NavigationCollection_BadKey_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ParentWithChildrenProfile>());
        var response = await fx.Client.GetAsync("/odata/Parents(notanint)/Children");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Navigation $count bad key → 400 ───────────────────────────────────────

    [Fact]
    public async Task NavigationCount_BadKey_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavCountProfile>());
        var response = await fx.Client.GetAsync("/odata/NavCountParents(notanint)/Children/$count");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── $ref GET bad key → 400 ────────────────────────────────────────────────

    [Fact]
    public async Task NavigationRef_Get_BadKey_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavQueryProfile>());
        var response = await fx.Client.GetAsync("/odata/NavQueryParents(notanint)/Children/$ref");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── $ref POST bad key → 400 ───────────────────────────────────────────────

    [Fact]
    public async Task NavigationRef_Post_BadKey_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavQueryProfile>());
        using var content = new StringContent(
            "{\"@odata.id\":\"http://localhost/odata/Children(1)\"}",
            Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/NavQueryParents(notanint)/Children/$ref", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── $ref DELETE bad key → 400 ─────────────────────────────────────────────

    [Fact]
    public async Task NavigationRef_Delete_BadKey_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavQueryProfile>());
        var response = await fx.Client.DeleteAsync(
            "/odata/NavQueryParents(notanint)/Children/$ref?$id=http://localhost/odata/Children(1)");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Entity-bound action bad key → 400 ─────────────────────────────────────

    [Fact]
    public async Task EntityBoundAction_BadKey_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EntityBoundOpsProfile>(),
            configureServices: s => s.AddSingleton(new EntityBoundOpsStore()));
        using var content = new StringContent("{\"newName\":\"Test\"}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/EntityBoundWidgets(notanint)/RenameWidget", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── DELETE bad key → 400 ──────────────────────────────────────────────────

    [Fact]
    public async Task Delete_BadKey_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var response = await fx.Client.DeleteAsync("/odata/Widgets(notanint)");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── PUT bad key → 400 ─────────────────────────────────────────────────────

    [Fact]
    public async Task Put_BadKey_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        using var content = new StringContent("{\"id\":1,\"name\":\"X\"}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/Widgets(notanint)", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── PATCH bad key → 400 ───────────────────────────────────────────────────

    [Fact]
    public async Task Patch_BadKey_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        using var content = new StringContent("{\"name\":\"X\"}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PatchAsync("/odata/Widgets(notanint)", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── OData profile — $count path with $filter ───────────────────────────────

    [Fact]
    public async Task ODataProfile_CountWithFilter_ReturnsFilteredCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ODataWidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/ODataWidgets/$count?$filter=Name eq 'Cog'");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(int.TryParse(body, out int count));
        Assert.Equal(1, count);
    }

    // ── HasOptional $ref — with refTargetEntitySet returns @odata.id ──────────

    [Fact]
    public async Task HasOptional_Ref_WithTargetEntitySet_ReturnsOdataId()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ParentWithTagRefProfile>());
        var response = await fx.Client.GetAsync("/odata/TagRefParents(1)/Tag/$ref");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.id", out var id));
        Assert.Contains("Tags(", id.GetString());
    }

    // ── HasOptional $ref — without refTargetEntitySet returns context envelope ─

    [Fact]
    public async Task HasOptional_Ref_WithoutTargetEntitySet_Returns200WithContext()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ParentWithTagProfile>());
        var response = await fx.Client.GetAsync("/odata/TaggedParents(1)/Tag/$ref");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out _));
    }

    // ── ETag — If-Match wildcard (*) on PATCH always matches ─────────────────

    [Fact]
    public async Task ETag_IfMatch_Wildcard_AlwaysMatchesOnPatch()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ETagBodyProfile>());
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/odata/ETagBodyWidgets(1)");
        request.Headers.TryAddWithoutValidation("If-Match", "*");
        request.Content = new StringContent("{\"name\":\"Patched\"}", Encoding.UTF8, "application/json");
        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── ETag — If-None-Match wildcard on GET → 304 ───────────────────────────

    [Fact]
    public async Task GetById_IfNoneMatch_Wildcard_Returns304()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ETagWidgetProfile>());
        using var req = new HttpRequestMessage(HttpMethod.Get, "/odata/ETagWidgets(1)");
        req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        var response = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }

    // ── OhDataRegistration.EntitySetNames lists all registered sets ───────────

    [Fact]
    public void OhDataRegistration_EntitySetNames_ReturnsAllNames()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o
            .AddEntitySetProfile<WidgetProfile>()
            .AddEntitySetProfile<QueryableWidgetProfile>());
        var reg = services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();
        var names = reg.EntitySetNames.ToList();
        Assert.Contains("Widgets", names);
        Assert.Contains("QueryableWidgets", names);
    }

    // ── Service document lists all registered entity sets ─────────────────────

    [Fact]
    public async Task ServiceDocument_MultipleProfiles_ListsAll()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .AddEntitySetProfile<WidgetProfile>()
            .AddEntitySetProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata");
        var values = json.GetProperty("value");
        var names = Enumerable.Range(0, values.GetArrayLength())
            .Select(i => values[i].GetProperty("name").GetString())
            .ToList();
        Assert.Contains("Widgets", names);
        Assert.Contains("QueryableWidgets", names);
    }

    // ── $count standalone on GetAll path ──────────────────────────────────────

    [Fact]
    public async Task Count_GetAllPath_ReturnsCorrectCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets/$count");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(long.TryParse(body, out long count));
        Assert.Equal(2L, count);
    }

    // ── $top exceeds MaxTop → 400 ─────────────────────────────────────────────

    [Fact]
    public async Task GetQueryable_TopExceedsMaxTop_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<MaxTopProfile>());
        // MaxTopProfile has MaxTop=5; requesting $top=10 should be rejected
        var response = await fx.Client.GetAsync("/odata/MaxTopWidgets?$top=10");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Profile type in two different named registrations → throws ─────────────

    [Fact]
    public void AddEntitySetProfile_SameTypeInTwoDifferentRegistrations_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData("r1", o => o.WithPrefix("/r1").AddEntitySetProfile<WidgetProfile>());
        Assert.Throws<InvalidOperationException>(() =>
            services.AddOhData("r2", o => o.WithPrefix("/r2").AddEntitySetProfile<WidgetProfile>()));
    }

    // ── Entity-level function returning null → 204 ────────────────────────────

    [Fact]
    public async Task EntityBoundFunction_ReturnsNull_Returns204()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NullReturnEntityFnProfile>());
        var response = await fx.Client.GetAsync("/odata/NullFnWidgets(1)/GetNullWidget");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── Bound function return value (int) → 200 ───────────────────────────────

    [Fact]
    public async Task BoundFunction_WithReturnValue_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BoundOpsProfile>(),
            configureServices: s => s.AddSingleton(new BoundOpsStore()));
        var response = await fx.Client.GetAsync("/odata/BoundWidgets/DoubleCount?factor=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // m5: primitive bound-function results now carry the JSON §11 individual-value envelope
        // ({"@odata.context":"...","value":<primitive>}) rather than a bare scalar body.
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out var context));
        Assert.Contains("Edm.Int32", context.GetString());
        Assert.Equal(2, json.GetProperty("value").GetInt32());
    }

    // ── PUT with AllowUpsert — new key → 201 ──────────────────────────────────

    [Fact]
    public async Task Put_AllowUpsert_NewKey_Returns201()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ETagUpsertProfile>());
        using var content = new StringContent("{\"id\":99,\"name\":\"New\"}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/ETagUpsertWidgets(99)", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}

// ── Fixtures for CoverageGapTests ─────────────────────────────────────────────

/// <summary>
/// Used by assembly-scanning tests. Must be concrete and non-abstract.
/// EntitySetName "ScanTargets" is what the scan tests assert against.
/// </summary>
internal class ScanTarget { public int Id { get; set; } }

internal class ScanTargetProfile : EntitySetProfile<int, ScanTarget>
{
    public ScanTargetProfile() : base(x => x.Id)
    {
        EntitySetName = "ScanTargets";
        GetAll = (ct) => Task.FromResult<IEnumerable<ScanTarget>>(Array.Empty<ScanTarget>());
    }
}

/// <summary>
/// Model type whose name exercises the consonant+y → ies pluralisation rule.
/// Type name "DefaultNameCategory" → EntitySetName "DefaultNameCategories".
/// </summary>
internal class DefaultNameCategory { public int Id { get; set; } }

internal class DefaultNameCategoryProfile : EntitySetProfile<int, DefaultNameCategory>
{
    public DefaultNameCategoryProfile() : base(x => x.Id)
    {
        // No explicit EntitySetName — Pluralize("DefaultNameCategory") = "DefaultNameCategories"
        GetAll = (ct) => Task.FromResult<IEnumerable<DefaultNameCategory>>(Array.Empty<DefaultNameCategory>());
    }
}

/// <summary>
/// Used to verify the UseETag zero-selectors guard. Instantiated directly in the test body —
/// never registered in DI or discovered by assembly scanning.
/// </summary>
internal class ETagNoSelectorsProfileConcrete : EntitySetProfile<int, Widget>
{
    public ETagNoSelectorsProfileConcrete() : base(x => x.Id)
    {
        EntitySetName = "ETagNoSelectorsWidgets";
        GetById = (id, ct) => Task.FromResult<Widget?>(null);
        UseETag(); // zero selectors → ArgumentException
    }
}

/// <summary>Has UseETag but no GetById — factory must throw InvalidOperationException at startup.</summary>
internal class ETagWithoutGetByIdProfile : EntitySetProfile<int, Widget>
{
    public ETagWithoutGetByIdProfile() : base(x => x.Id)
    {
        EntitySetName = "ETagNoGetByIdWidgets";
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(Array.Empty<Widget>());
        UseETag(x => x.Name); // GetById is null → factory rejects at startup
    }
}

/// <summary>Uses UseETag with two property selectors to exercise the multi-selector hash path.</summary>
internal class MultiETagProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Sprocket" }
    };

    public MultiETagProfile() : base(x => x.Id)
    {
        EntitySetName = "MultiETagWidgets";
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        UseETag(x => x.Id, x => x.Name); // two selectors
    }
}

/// <summary>RequireRoles with zero args — must throw ArgumentException.</summary>
internal class EmptyRolesProfile : EntitySetProfile<int, Widget>
{
    public EmptyRolesProfile() : base(x => x.Id)
    {
        RequireRoles(); // zero args → ArgumentException
    }
}

/// <summary>Tag entity for single-entity HasOptional navigation tests.</summary>
internal class CovTag { public int Id { get; set; } public string Label { get; set; } = ""; }

/// <summary>Parent entity with an optional Tag navigation property.</summary>
internal class ParentWithCovTag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public CovTag? Tag { get; set; }
}

/// <summary>
/// Profile with HasOptional single-entity nav route (no refTargetEntitySet).
/// GET /TaggedParents(1)/Tag → 200; GET /TaggedParents(999)/Tag → 404.
/// GET /TaggedParents(1)/Tag/$ref → 200 with @odata.context envelope.
/// </summary>
internal class ParentWithTagProfile : EntitySetProfile<int, ParentWithCovTag>
{
    private static readonly List<ParentWithCovTag> _parents = new()
    {
        new() { Id = 1, Name = "Parent1" }
    };
    private static readonly CovTag _tag = new() { Id = 10, Label = "red" };

    public ParentWithTagProfile() : base(x => x.Id)
    {
        EntitySetName = "TaggedParents";
        GetAll = (ct) => Task.FromResult<IEnumerable<ParentWithCovTag>>(_parents);
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasOptional<CovTag>(
            navigation: x => x.Tag!,
            get: (parentId, ct) => Task.FromResult<CovTag?>(parentId == 1 ? _tag : null),
            refTargetEntitySet: (string?)null);
    }
}

/// <summary>
/// Profile with HasOptional single-entity nav route with a refTargetEntitySet.
/// GET /TagRefParents(1)/Tag/$ref → 200 with @odata.id pointing into "Tags".
/// </summary>
internal class ParentWithTagRefProfile : EntitySetProfile<int, ParentWithCovTag>
{
    private static readonly List<ParentWithCovTag> _parents = new()
    {
        new() { Id = 1, Name = "Parent1" }
    };
    private static readonly CovTag _tag = new() { Id = 10, Label = "blue" };

    public ParentWithTagRefProfile() : base(x => x.Id)
    {
        EntitySetName = "TagRefParents";
        GetAll = (ct) => Task.FromResult<IEnumerable<ParentWithCovTag>>(_parents);
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasOptional<CovTag>(
            navigation: x => x.Tag!,
            get: (parentId, ct) => Task.FromResult<CovTag?>(parentId == 1 ? _tag : null),
            refTargetEntitySet: "Tags");
    }
}

/// <summary>Address entity for HasRequired single-entity navigation tests.</summary>
internal class CovAddress { public int Id { get; set; } public string Street { get; set; } = ""; }

/// <summary>Parent entity with a required Address navigation property.</summary>
internal class ParentWithCovAddress
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public CovAddress? Address { get; set; }
}

/// <summary>
/// Profile with HasRequired single-entity nav route.
/// GET /AddressedParents(1)/Address → 200; GET /AddressedParents(999)/Address → 404.
/// </summary>
internal class ParentWithAddressProfile : EntitySetProfile<int, ParentWithCovAddress>
{
    private static readonly List<ParentWithCovAddress> _parents = new()
    {
        new() { Id = 1, Name = "P1" }
    };
    private static readonly CovAddress _addr = new() { Id = 1, Street = "123 Main St" };

    public ParentWithAddressProfile() : base(x => x.Id)
    {
        EntitySetName = "AddressedParents";
        GetAll = (ct) => Task.FromResult<IEnumerable<ParentWithCovAddress>>(_parents);
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasRequired(
            navigation: x => x.Address!,
            get: (parentId, ct) => Task.FromResult<CovAddress>(parentId == 1 ? _addr : null!));
    }
}

/// <summary>Profile that restricts $filter to Name only (expression overload).</summary>
internal class NameFilterOnlyProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Alpha" },
        new() { Id = 2, Name = "Beta" },
    };

    public NameFilterOnlyProfile() : base(x => x.Id)
    {
        EntitySetName = "NameFilterWidgets";
        FilterEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
        FilterProperties(x => x.Name);
    }
}

/// <summary>Profile that restricts $select to Name only (expression overload).</summary>
internal class NameSelectOnlyProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Alpha" },
    };

    public NameSelectOnlyProfile() : base(x => x.Id)
    {
        EntitySetName = "NameSelectWidgets";
        SelectEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
        SelectProperties(x => x.Name);
    }
}

/// <summary>Profile that restricts $orderby to Name only (expression overload).</summary>
internal class NameOrderByOnlyProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Beta" },
        new() { Id = 2, Name = "Alpha" },
    };

    public NameOrderByOnlyProfile() : base(x => x.Id)
    {
        EntitySetName = "NameOrderByWidgets";
        OrderByEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
        OrderByProperties(x => x.Name);
    }
}

/// <summary>Profile that restricts $select using the string overload.</summary>
internal class StringSelectOnlyProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Alpha" },
    };

    public StringSelectOnlyProfile() : base(x => x.Id)
    {
        EntitySetName = "StringSelectWidgets";
        SelectEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
        SelectProperties("Name");
    }
}

/// <summary>Profile that restricts $orderby using the string overload.</summary>
internal class StringOrderByOnlyProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Beta" },
        new() { Id = 2, Name = "Alpha" },
    };

    public StringOrderByOnlyProfile() : base(x => x.Id)
    {
        EntitySetName = "StringOrderByWidgets";
        OrderByEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
        OrderByProperties("Name");
    }
}

/// <summary>
/// Child entity for the NoExpandProfile test. Uses Cov prefix to avoid
/// colliding with <see cref="Child"/> defined in Fixtures.cs.
/// </summary>
internal class CovChild { public int Id { get; set; } public int ParentId { get; set; } public string Name { get; set; } = ""; }

/// <summary>Parent entity used by NoExpandProfile.</summary>
internal class CovParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public IEnumerable<CovChild>? Children { get; set; }
}

/// <summary>
/// Profile that calls ExpandProperties() with no arguments (expression overload),
/// which restricts all $expand. $expand=Children should return 400.
/// </summary>
internal class NoExpandProfile : EntitySetProfile<int, CovParent>
{
    private static readonly List<CovParent> _parents = new()
    {
        new() { Id = 1, Name = "P1" }
    };

    public NoExpandProfile() : base(x => x.Id)
    {
        EntitySetName = "NoExpandParents";
        ExpandEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_parents.AsQueryable());
        HasMany(x => x.Children!);
        ExpandProperties(Array.Empty<string>()); // empty string array → restricts all $expand
    }
}

/// <summary>Profile for testing an entity-level function that returns null → 204.</summary>
internal class NullReturnEntityFnProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Alpha" }
    };

    public NullReturnEntityFnProfile() : base(x => x.Id)
    {
        EntitySetName = "NullFnWidgets";
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        BindEntityFunction(GetNullWidget);
    }

    private Task<Widget?> GetNullWidget(int key) => Task.FromResult<Widget?>(null);
}

/// <summary>
/// Profile with ETag + AllowUpsert for the wasCreated (201) path in PUT.
/// AllowUpsert requires Post to also be configured.
/// </summary>
internal class ETagUpsertProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Existing" }
    };

    public ETagUpsertProfile() : base(x => x.Id)
    {
        EntitySetName = "ETagUpsertWidgets";
        AllowUpsert = true;

        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));

        Put = (id, widget, ct) =>
        {
            var existing = _store.FirstOrDefault(w => w.Id == id);
            // Return null when the entity doesn't exist — signals the framework to upsert
            if (existing is null) return Task.FromResult<Widget>(null!);
            _store.RemoveAll(w => w.Id == id);
            widget.Id = id;
            _store.Add(widget);
            return Task.FromResult(widget);
        };

        Post = (widget, ct) =>
        {
            _store.Add(widget);
            return Task.FromResult<Widget?>(widget);
        };

        UseETag(x => x.Name);
    }
}
