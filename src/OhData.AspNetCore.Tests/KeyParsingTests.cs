using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

public class KeyParsingTests
{
    // ── int ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IntKey_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets(1)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task IntKey_Missing_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets(9999)");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BadIntKey_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets(notanint)");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── long ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LongKey_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<LongKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/LongItems(9999999999)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LongKey_Missing_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<LongKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/LongItems(1)");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── short ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ShortKey_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ShortKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/ShortItems(32767)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── byte ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ByteKey_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ByteKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/ByteItems(255)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── bool ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BoolKey_True_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<BoolKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/BoolItems(true)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BoolKey_False_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<BoolKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/BoolItems(false)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── float ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FloatKey_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<FloatKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/FloatItems(1.5)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── double ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DoubleKey_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DoubleKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/DoubleItems(3.14)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── decimal ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DecimalKey_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DecimalKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/DecimalItems(1.5)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Guid ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GuidKey_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<GadgetProfile>());
        var response = await fx.Client.GetAsync($"/odata/Gadgets({GadgetProfile.KnownId})");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── string ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StringKey_StrippedOfQuotes()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ThingProfile>());
        var response = await fx.Client.GetAsync("/odata/Things('alpha')");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetById_StringKey_EscapedQuotes_Returns200()
    {
        // OData spec: single quotes within string keys are escaped as ''
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ThingProfile>());
        var response = await fx.Client.GetAsync("/odata/Things('O''Brien')");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── DateTime ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task KeyParser_DateTime_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DateTimeKeyProfile>());
        string key = Uri.EscapeDataString("2024-06-01T00:00:00");
        var response = await fx.Client.GetAsync($"/odata/DateTimeItems({key})");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── DateTimeOffset ──────────────────────────────────────────────────────────

    [Fact]
    public async Task KeyParser_DateTimeOffset_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DateTimeOffsetKeyProfile>());
        string key = Uri.EscapeDataString("2024-01-15T12:00:00Z");
        var response = await fx.Client.GetAsync($"/odata/DateTimeOffsetItems({key})");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── DateOnly ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task KeyParser_DateOnly_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DateOnlyKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/DateOnlyItems(2024-03-20)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── TimeOnly ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TimeOnlyKey_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<TimeOnlyKeyProfile>());
        // Colons must be percent-encoded in path segments; ASP.NET Core decodes before binding.
        string key = Uri.EscapeDataString("08:30:00");
        var response = await fx.Client.GetAsync($"/odata/TimeOnlyItems({key})");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TimeOnlyKey_Missing_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<TimeOnlyKeyProfile>());
        string key = Uri.EscapeDataString("12:00:00");
        var response = await fx.Client.GetAsync($"/odata/TimeOnlyItems({key})");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── enum ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnumKey_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<StatusItemProfile>());
        // Enum keys are formatted as their underlying integer value by ODataKeyFormatter.
        var response = await fx.Client.GetAsync("/odata/StatusItems(1)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EnumKey_Missing_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<StatusItemProfile>());
        var response = await fx.Client.GetAsync("/odata/StatusItems(99)");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Nullable<T> ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task NullableIntKey_WithValue_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NullableIntKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/NullableIntItems(42)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NullableIntKey_NullLiteral_Returns404()
    {
        // "null" is a valid OData nullable key literal; our store has no null-keyed entity.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NullableIntKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/NullableIntItems(null)");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Supporting fixtures ────────────────────────────────────────────────────

    private class Gadget { public Guid Id { get; set; } }

    private class GadgetProfile : EntitySetProfile<Guid, Gadget>
    {
        public static readonly Guid KnownId = Guid.NewGuid();
        private static readonly List<Gadget> Store = new() { new() { Id = KnownId } };

        public GadgetProfile() : base(x => x.Id)
        {
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(g => g.Id == id));
        }
    }

    private class Thing { public string Id { get; set; } = ""; }

    private class ThingProfile : EntitySetProfile<string, Thing>
    {
        private static readonly List<Thing> Store = new() { new() { Id = "alpha" }, new() { Id = "O'Brien" } };

        public ThingProfile() : base(x => x.Id)
        {
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(t => t.Id == id));
        }
    }

    private class LongItem { public long Id { get; set; } public string Name { get; set; } = ""; }
    private class LongKeyProfile : EntitySetProfile<long, LongItem>
    {
        private static readonly LongItem Known = new() { Id = 9_999_999_999L, Name = "BigItem" };
        public LongKeyProfile() : base(x => x.Id)
        {
            EntitySetName = "LongItems";
            GetById = (id, ct) => Task.FromResult(id == Known.Id ? Known : null);
        }
    }

    private class ShortItem { public short Id { get; set; } public string Name { get; set; } = ""; }
    private class ShortKeyProfile : EntitySetProfile<short, ShortItem>
    {
        private static readonly ShortItem Known = new() { Id = 32767, Name = "MaxShort" };
        public ShortKeyProfile() : base(x => x.Id)
        {
            EntitySetName = "ShortItems";
            GetById = (id, ct) => Task.FromResult(id == Known.Id ? Known : null);
        }
    }

    private class ByteItem { public byte Id { get; set; } public string Name { get; set; } = ""; }
    private class ByteKeyProfile : EntitySetProfile<byte, ByteItem>
    {
        private static readonly ByteItem Known = new() { Id = 255, Name = "MaxByte" };
        public ByteKeyProfile() : base(x => x.Id)
        {
            EntitySetName = "ByteItems";
            GetById = (id, ct) => Task.FromResult(id == Known.Id ? Known : null);
        }
    }

    private class BoolItem { public bool Id { get; set; } public string Name { get; set; } = ""; }
    private class BoolKeyProfile : EntitySetProfile<bool, BoolItem>
    {
        private static readonly List<BoolItem> Store = new()
        {
            new() { Id = true, Name = "Yes" },
            new() { Id = false, Name = "No" },
        };
        public BoolKeyProfile() : base(x => x.Id)
        {
            EntitySetName = "BoolItems";
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(x => x.Id == id));
        }
    }

    private class FloatItem { public float Id { get; set; } public string Name { get; set; } = ""; }
    private class FloatKeyProfile : EntitySetProfile<float, FloatItem>
    {
        private static readonly FloatItem Known = new() { Id = 1.5f, Name = "OnePointFive" };
        public FloatKeyProfile() : base(x => x.Id)
        {
            EntitySetName = "FloatItems";
            GetById = (id, ct) => Task.FromResult(id == Known.Id ? Known : null);
        }
    }

    private class DoubleItem { public double Id { get; set; } public string Name { get; set; } = ""; }
    private class DoubleKeyProfile : EntitySetProfile<double, DoubleItem>
    {
        private static readonly DoubleItem Known = new() { Id = 3.14, Name = "Pi" };
        public DoubleKeyProfile() : base(x => x.Id)
        {
            EntitySetName = "DoubleItems";
            GetById = (id, ct) => Task.FromResult(id == Known.Id ? Known : null);
        }
    }

    private class TimeOnlyItem { public TimeOnly Id { get; set; } public string Name { get; set; } = ""; }
    private class TimeOnlyKeyProfile : EntitySetProfile<TimeOnly, TimeOnlyItem>
    {
        private static readonly TimeOnly KnownKey = new(8, 30, 0);
        private static readonly TimeOnlyItem Known = new() { Id = KnownKey, Name = "Morning" };
        public TimeOnlyKeyProfile() : base(x => x.Id)
        {
            EntitySetName = "TimeOnlyItems";
            GetById = (id, ct) => Task.FromResult(id == Known.Id ? Known : null);
        }
    }

    private enum ItemStatus { Active = 1, Inactive = 2 }
    private class StatusItem { public ItemStatus Id { get; set; } public string Name { get; set; } = ""; }
    private class StatusItemProfile : EntitySetProfile<ItemStatus, StatusItem>
    {
        private static readonly List<StatusItem> Store = new()
        {
            new() { Id = ItemStatus.Active, Name = "Active" },
            new() { Id = ItemStatus.Inactive, Name = "Inactive" },
        };
        public StatusItemProfile() : base(x => x.Id)
        {
            EntitySetName = "StatusItems";
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(x => x.Id == id));
        }
    }

    private class NullableIntItem { public int? Id { get; set; } public string Name { get; set; } = ""; }
    private class NullableIntKeyProfile : EntitySetProfile<int?, NullableIntItem>
    {
        private static readonly NullableIntItem Known = new() { Id = 42, Name = "Item" };
        public NullableIntKeyProfile() : base(x => x.Id)
        {
            EntitySetName = "NullableIntItems";
            GetById = (id, ct) => Task.FromResult(id == Known.Id ? Known : null);
        }
    }
}
