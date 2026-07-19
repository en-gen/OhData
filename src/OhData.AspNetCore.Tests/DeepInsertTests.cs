using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Tests for deep insert — nested related entities in <c>POST /{EntitySet}</c>
/// (OData §11.4.2.2). Rides the existing <c>Post</c> handler; no new handler delegate. Gated by
/// the new <c>AllowDeepInsert</c> profile flag (default <c>false</c>, entity-level granularity).
/// <para>
/// Default (<c>false</c>): System.Text.Json already binds nested navigation values into the
/// deserialized model during the existing POST pipeline; the framework strips them (sets them to
/// <c>null</c>) before <c>Post</c> is invoked, so a handler that doesn't expect a graph never
/// silently persists only part of one.
/// </para>
/// <para>
/// Opt-in (<c>true</c>): the full deserialized graph is passed to <c>Post</c> as-is. The handler
/// owns atomic persistence of the whole graph.
/// </para>
/// <para>
/// <c>@odata.bind</c> (linking to an existing entity, JSON format §8.5) is documented
/// non-support: a request body containing the annotation anywhere is rejected with
/// <c>501 Not Implemented</c> rather than silently ignored.
/// </para>
/// </summary>
public class DeepInsertTests
{
    // ── Default (AllowDeepInsert = false): nested navigation values are stripped ────

    [Fact]
    public async Task Post_Default_StripsNestedCollectionNav_BeforeHandlerSeesIt()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DeepInsertDefaultProfile>());

        var response = await fx.Client.PostAsJsonAsync("/odata/DeepInsertDefaultOrders", new
        {
            customer = "Alice",
            lines = new[] { new { sku = "WIDGET-1", quantity = 2 } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Instrumented via a capturing fixture: assert the handler itself received a stripped
        // graph, not merely that the response happens to omit it. The framework nulls the
        // navigation property out entirely (rather than substituting an empty collection).
        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByHandler);
        Assert.Null(DeepInsertDefaultProfile.LastReceivedByHandler!.Lines);

        // #240: the POST echo omits the un-expanded navigation entirely (matching a read of the
        // same type), rather than leaking it as an explicit null.
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.TryGetProperty("lines", out _));
    }

    [Fact]
    public async Task Post_Default_StripsNestedSingleValuedNav_BeforeHandlerSeesIt()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DeepInsertDefaultProfile>());

        var response = await fx.Client.PostAsJsonAsync("/odata/DeepInsertDefaultOrders", new
        {
            customer = "Bob",
            category = new { name = "Hardware" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByHandler);
        Assert.Null(DeepInsertDefaultProfile.LastReceivedByHandler!.Category);

        // #240: the stripped single-valued navigation is omitted from the echo, not echoed as null.
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.TryGetProperty("category", out _));
    }

    [Fact]
    public async Task Post_Default_NonNavigationCollectionProperty_Survives()
    {
        // Only CLR properties declared as navigations via HasMany/HasOptional/HasRequired are
        // stripped. A plain (non-nav) collection property is left untouched even when the
        // profile has not opted into deep insert.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DeepInsertDefaultProfile>());

        var response = await fx.Client.PostAsJsonAsync("/odata/DeepInsertDefaultOrders", new
        {
            customer = "Carol",
            tags = new[] { "rush", "gift-wrap" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByHandler);
        Assert.Equal(new[] { "rush", "gift-wrap" }, DeepInsertDefaultProfile.LastReceivedByHandler!.Tags);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("tags").GetArrayLength());
    }

    // ── Opt-in (AllowDeepInsert = true): full graph passed through, echoed in response ──

    [Fact]
    public async Task Post_OptIn_PassesFullGraphToHandler_AndEchoesChildrenInResponse()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DeepInsertOptInProfile>());

        var response = await fx.Client.PostAsJsonAsync("/odata/DeepInsertOptInOrders", new
        {
            customer = "Dave",
            lines = new[]
            {
                new { sku = "WIDGET-1", quantity = 2 },
                new { sku = "GADGET-9", quantity = 1 },
            },
            category = new { name = "Electronics" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The handler-owned persistence path received the whole graph, not a stripped parent.
        Assert.NotNull(DeepInsertOptInProfile.LastReceivedByHandler);
        Assert.Equal(2, DeepInsertOptInProfile.LastReceivedByHandler!.Lines.Count);
        Assert.NotNull(DeepInsertOptInProfile.LastReceivedByHandler!.Category);

        // §11.4.2.2: the 201 response echoes the created graph, nested values serialized inline.
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var lines = json.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal("WIDGET-1", lines[0].GetProperty("sku").GetString());
        Assert.Equal("GADGET-9", lines[1].GetProperty("sku").GetString());
        Assert.Equal("Electronics", json.GetProperty("category").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Post_OptIn_ReturnMinimal_Returns204WithODataEntityId_AndStillPersistsGraph()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DeepInsertOptInProfile>());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/odata/DeepInsertOptInOrders")
        {
            Content = JsonContent.Create(new
            {
                customer = "Erin",
                lines = new[] { new { sku = "SPROCKET-3", quantity = 5 } },
            }),
        };
        request.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

        var response = await fx.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.Contains("OData-EntityId"));
        Assert.True(response.Headers.Contains("Preference-Applied"));
        Assert.Equal("return=minimal", response.Headers.GetValues("Preference-Applied").First());

        // 204 has no body, but the handler still received (and, per contract, persisted) the
        // full graph.
        Assert.NotNull(DeepInsertOptInProfile.LastReceivedByHandler);
        Assert.Single(DeepInsertOptInProfile.LastReceivedByHandler!.Lines);
    }

    // ── @odata.bind: documented non-support → 501 ────────────────────────────────

    [Fact]
    public async Task Post_ODataBindAnnotation_Returns501NotImplemented()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DeepInsertOptInProfile>());

        using var content = new StringContent(
            "{\"customer\":\"Frank\",\"category@odata.bind\":\"DeepInsertOptInCategories(1)\"}",
            Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/DeepInsertOptInOrders", content);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out var err));
        Assert.Equal("NotImplemented", err.GetProperty("code").GetString());

        // The connection must remain usable after a 501 — no partial write occurred.
        var followUp = await fx.Client.GetAsync("/odata/DeepInsertOptInOrders");
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
    }

    [Fact]
    public async Task Post_ODataBindAnnotation_Returns501_EvenWhenDeepInsertDisabled()
    {
        // @odata.bind is rejected regardless of AllowDeepInsert — it is not silently ignored in
        // either mode.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DeepInsertDefaultProfile>());

        using var content = new StringContent(
            "{\"customer\":\"Grace\",\"category@odata.bind\":\"DeepInsertDefaultCategories(1)\"}",
            Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/DeepInsertDefaultOrders", content);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task Post_ODataBindAnnotation_NestedInsideChild_Returns501()
    {
        // The annotation is detected anywhere in the body, not just at the top level.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DeepInsertOptInProfile>());

        using var content = new StringContent(
            "{\"customer\":\"Heidi\",\"lines\":[{\"sku\":\"X\",\"product@odata.bind\":\"Products(1)\"}]}",
            Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/DeepInsertOptInOrders", content);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    // ── Coexists with a profile that also has PostChild / batch nav handlers ────────

    [Fact]
    public async Task Post_CoexistsWithPostChildAndBatchNavHandlersOnSameProfile()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DeepInsertWithNavHandlersProfile>());

        // Deep insert on the entity-level POST route.
        var createResponse = await fx.Client.PostAsJsonAsync("/odata/DeepInsertNavOrders", new
        {
            customer = "Ivan",
            lines = new[] { new { sku = "WIDGET-1", quantity = 1 } },
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, created.GetProperty("lines").GetArrayLength());
        int orderId = created.GetProperty("id").GetInt32();

        // POST-to-nav (PostChild, §11.4.2.1) still works on the same profile.
        var postChildResponse = await fx.Client.PostAsJsonAsync(
            $"/odata/DeepInsertNavOrders({orderId})/Notes", new { text = "Handle with care" });
        Assert.Equal(HttpStatusCode.Created, postChildResponse.StatusCode);

        // Batch-loaded nav route (GET) still works on the same profile.
        var linesResponse = await fx.Client.GetAsync($"/odata/DeepInsertNavOrders({orderId})/Lines");
        Assert.Equal(HttpStatusCode.OK, linesResponse.StatusCode);
        var linesJson = await linesResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, linesJson.GetProperty("value").GetArrayLength());
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────

    private class DeepInsertLine
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public string Sku { get; set; } = "";
        public int Quantity { get; set; }
    }

    private class DeepInsertCategory
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class DeepInsertOrder
    {
        public int Id { get; set; }
        public string Customer { get; set; } = "";
        public List<DeepInsertLine> Lines { get; set; } = new();
        public DeepInsertCategory? Category { get; set; }

        // Deliberately NOT declared via HasMany/HasOptional/HasRequired — a plain collection
        // property, not a navigation. Must survive stripping in every mode.
        public List<string> Tags { get; set; } = new();
    }

    /// <summary>AllowDeepInsert left at its default (false) — nested nav values are stripped.</summary>
    private class DeepInsertDefaultProfile : EntitySetProfile<int, DeepInsertOrder>
    {
        private static int _nextId = 1;
        private readonly List<DeepInsertOrder> _orders = new();

        // Static so the test can observe exactly what the handler received, independent of
        // whatever the framework echoes back in the HTTP response.
        public static DeepInsertOrder? LastReceivedByHandler;

        public DeepInsertDefaultProfile() : base(x => x.Id)
        {
            EntitySetName = "DeepInsertDefaultOrders";

            // Declared as navigations (EDM-only, no route) so they participate in the strip set.
            HasMany(x => x.Lines);
            HasOptional(x => x.Category!);

            GetAll = (_) => Task.FromResult<IEnumerable<DeepInsertOrder>>(_orders);

            Post = (order, _) =>
            {
                LastReceivedByHandler = order;
                order.Id = _nextId++;
                _orders.Add(order);
                return Task.FromResult<DeepInsertOrder?>(order);
            };
        }
    }

    /// <summary>AllowDeepInsert = true — full graph passed through; handler owns persistence.</summary>
    private class DeepInsertOptInProfile : EntitySetProfile<int, DeepInsertOrder>
    {
        private static int _nextId = 1;
        private readonly List<DeepInsertOrder> _orders = new();

        public static DeepInsertOrder? LastReceivedByHandler;

        public DeepInsertOptInProfile() : base(x => x.Id)
        {
            EntitySetName = "DeepInsertOptInOrders";
            AllowDeepInsert = true;

            HasMany(x => x.Lines);
            HasOptional(x => x.Category!);

            GetAll = (_) => Task.FromResult<IEnumerable<DeepInsertOrder>>(_orders);

            Post = (order, _) =>
            {
                LastReceivedByHandler = order;
                order.Id = _nextId++;
                int lineId = 1;
                foreach (var line in order.Lines)
                {
                    line.Id = lineId++;
                    line.OrderId = order.Id;
                }
                if (order.Category is not null) order.Category.Id = 1;
                // "Atomic persistence" stand-in: a single in-memory add representing the whole
                // graph, mirroring the contract of a single EF Core SaveChanges call.
                _orders.Add(order);
                return Task.FromResult<DeepInsertOrder?>(order);
            };
        }
    }

    private class DeepInsertNote
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public string Text { get; set; } = "";
    }

    private class DeepInsertOrderWithNotes
    {
        public int Id { get; set; }
        public string Customer { get; set; } = "";
        public List<DeepInsertLine> Lines { get; set; } = new();
        public List<DeepInsertNote> Notes { get; set; } = new();
    }

    /// <summary>
    /// Deep insert (entity-level POST, opted in) on a profile that ALSO has a batch-loaded nav
    /// route (Lines) and a PostChild nav route (Notes, §11.4.2.1) — verifies the three POST-ish
    /// pipelines (entity POST/deep-insert, POST-to-nav, GET-nav) don't collide or interfere.
    /// </summary>
    private class DeepInsertWithNavHandlersProfile : EntitySetProfile<int, DeepInsertOrderWithNotes>
    {
        // Static: profiles are registered AddScoped, so each HTTP request resolves a fresh
        // profile instance. Backing "storage" must be static (shared) for state written by one
        // request (the deep-insert POST) to be observable by a later request (POST-to-nav, GET).
        private static int _nextId = 1;
        private static int _nextNoteId = 1;
        private static readonly List<DeepInsertOrderWithNotes> _orders = new();
        private static readonly List<DeepInsertNote> _notes = new();

        public DeepInsertWithNavHandlersProfile() : base(x => x.Id)
        {
            EntitySetName = "DeepInsertNavOrders";
            AllowDeepInsert = true;

            HasMany(x => x.Lines, batchGetAll: (orderIds, ct) =>
            {
                var lookup = _orders
                    .Where(o => orderIds.Contains(o.Id))
                    .SelectMany(o => o.Lines)
                    .ToLookup(l => l.OrderId);
                return Task.FromResult(lookup);
            });

            HasMany(
                navigation: x => x.Notes,
                getAll: (orderId, ct) => Task.FromResult<IEnumerable<DeepInsertNote>>(_notes.Where(n => n.OrderId == orderId)),
                post: (orderId, note, ct) =>
                {
                    if (_orders.All(o => o.Id != orderId)) return Task.FromResult<DeepInsertNote?>(null);
                    note.Id = _nextNoteId++;
                    note.OrderId = orderId;
                    _notes.Add(note);
                    return Task.FromResult<DeepInsertNote?>(note);
                });

            GetById = (id, _) => Task.FromResult(_orders.FirstOrDefault(o => o.Id == id));

            Post = (order, _) =>
            {
                order.Id = _nextId++;
                int lineId = 1;
                foreach (var line in order.Lines)
                {
                    line.Id = lineId++;
                    line.OrderId = order.Id;
                }
                _orders.Add(order);
                return Task.FromResult<DeepInsertOrderWithNotes?>(order);
            };
        }
    }
}
