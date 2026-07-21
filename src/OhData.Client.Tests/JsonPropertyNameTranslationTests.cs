using System;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using OhData.Client.Internal;
using Xunit;

namespace OhData.Client.Tests;

/// <summary>
/// #253: the client's $filter/$select/$orderby translators emit a property's
/// <c>[System.Text.Json.Serialization.JsonPropertyName]</c> name (verbatim, ahead of the naming
/// policy) so query-option property names match the server's EDM/$metadata name.
/// </summary>
public class JsonPropertyNameTranslationTests
{
    private sealed class Customer
    {
        [JsonPropertyName("emailAddress")]
        public string Email { get; set; } = "";

        public string Name { get; set; } = "";
    }

    // ── $select ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Select_RenamedProperty_EmitsJsonName()
    {
        string result = SelectTranslator.Translate<Customer>(x => x.Email);
        Assert.Equal("emailAddress", result);
    }

    [Fact]
    public void Select_RenamedProperty_JsonNameWinsOverCamelCasePolicy()
    {
        // [JsonPropertyName] is emitted verbatim — the naming policy must NOT re-case it.
        string result = SelectTranslator.Translate<Customer>(x => x.Email, JsonNamingPolicy.CamelCase);
        Assert.Equal("emailAddress", result);
    }

    [Fact]
    public void Select_UnrenamedProperty_UsesPolicy()
    {
        Assert.Equal("Name", SelectTranslator.Translate<Customer>(x => x.Name));
        Assert.Equal("name", SelectTranslator.Translate<Customer>(x => x.Name, JsonNamingPolicy.CamelCase));
    }

    // ── $filter ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Filter_RenamedProperty_EmitsJsonName()
    {
        Expression<Func<Customer, bool>> predicate = x => x.Email == "ada@example.com";
        string result = FilterTranslator.Translate(predicate);
        Assert.Equal("emailAddress eq 'ada@example.com'", result);
    }

    [Fact]
    public void Filter_RenamedProperty_JsonNameWinsOverCamelCasePolicy()
    {
        Expression<Func<Customer, bool>> predicate = x => x.Email == "ada@example.com";
        string result = FilterTranslator.Translate(predicate, JsonNamingPolicy.CamelCase);
        Assert.Equal("emailAddress eq 'ada@example.com'", result);
    }
}
