using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #372 — <c>Preference-Applied</c> echoes OData 4.0's <c>odata.maxpagesize</c>, not 4.01's bare
/// <c>maxpagesize</c>.
/// <para>
/// The bare token is the 4.01 rename. This service reports <c>OData-Version: 4.0</c> and Protocol
/// §8.2.8.5 spells the 4.0 preference <c>odata.maxpagesize</c>, so echoing the bare form claimed the
/// server had applied a preference that version does not define.
/// </para>
/// <para>
/// The pre-existing assertions on this header all used <c>Contains("maxpagesize=N")</c>, which
/// matches the prefixed spelling too — so they neither caught the defect nor pin the fix. These
/// assert the prefix explicitly, and that the bare token is no longer emitted on its own.
/// </para>
/// </summary>
public sealed class Issue372PreferenceAppliedTokenTests
{
    private static async Task<string> AppliedAsync(TestFixture fx, string url, string preferValue)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Prefer", preferValue);
        HttpResponseMessage response = await fx.Client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("Preference-Applied", out var values),
            $"no Preference-Applied on {url} with Prefer: {preferValue}");
        return string.Join(",", values!);
    }

    [Theory]
    [InlineData("odata.maxpagesize=3")]   // the 4.0 spelling a conforming client sends
    [InlineData("maxpagesize=3")]         // the 4.01 spelling is still ACCEPTED on the request
    public async Task TheEchoUsesTheOData40Token_WhicheverSpellingWasSent(string prefer)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());

        string applied = await AppliedAsync(fx, "/odata/Widgets", prefer);

        Assert.Contains("odata.maxpagesize=3", applied, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBareTokenIsNoLongerEmitted()
    {
        // The sharp assertion: "contains maxpagesize=3" passes for BOTH spellings, which is why the
        // existing suite could not tell them apart. This one cannot pass for the bare form.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());

        string applied = await AppliedAsync(fx, "/odata/Widgets", "odata.maxpagesize=3");

        Assert.DoesNotContain(
            applied.Split(',').Select(part => part.Trim()),
            part => part.StartsWith("maxpagesize=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReturnPreferencesAreUnchanged()
    {
        // The control: `return=minimal`/`return=representation` carry no `odata.` prefix in either
        // version (§8.2.8.4), so this change must not touch them.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/odata/Widgets")
        {
            Content = new StringContent(
                "{\"Id\":9001,\"Name\":\"w\"}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

        HttpResponseMessage response = await fx.Client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("Preference-Applied", out var values));
        Assert.Contains("return=minimal", string.Join(",", values!), StringComparison.Ordinal);
    }
}
