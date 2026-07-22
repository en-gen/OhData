# Error handling & literal types

Part of the [OhData.Client guide](index.md).

## Error handling

Non-2xx responses throw `ODataClientException`, which parses the OData error envelope:

```csharp
try
{
    await client.For<Product>().Key(99999).GetAsync();
}
catch (ODataClientException ex) when (ex.StatusCode == 404)
{
    Console.WriteLine(ex.ODataErrorMessage);  // "Widget with key '99999' was not found."
}
catch (ODataClientException ex)
{
    Console.WriteLine($"HTTP {ex.StatusCode}: [{ex.ODataErrorCode}] {ex.ODataErrorMessage}");
}
```

`ODataClientException` properties:
- `StatusCode` — the HTTP status code as an `int` (e.g. `404`, `412`)
- `ODataErrorCode` — the `"code"` field from the OData error body, or an empty string if the response was not an OData error envelope
- `ODataErrorMessage` — the `"message"` field, or the raw response body if not a valid OData error

## Literal type support

The filter translator and key formatter handle these CLR types:

| CLR type | OData literal | Example |
|----------|--------------|---------|
| `string` | `'value'` (single-quoted, `'` → `''`) | `Name eq 'it''s'` |
| `int`, `long`, `short` | unquoted decimal | `Id eq 42` |
| `decimal`, `float`, `double` | invariant-culture decimal | `Price gt 4.99` |
| `bool` | `true` / `false` | `IsActive eq true` |
| `Guid` | 36-char hex | `Id eq 3f2504e0-...` |
| `DateTime` / `DateTimeOffset` | ISO 8601, always with `Z` or an explicit offset | `CreatedAt gt 2024-01-01T00:00:00Z` |
| `DateOnly` | `'yyyy-MM-dd'` | `Date eq 2024-06-15` |
| `TimeOnly` | `'HH:mm:ss'` | `Time eq 09:30:00` |
| `Enum` | quoted member name | `Status eq 'Active'` |

**`DateTime` kind semantics.** OData `Edm.DateTimeOffset` literals always require a `Z` or an
explicit numeric offset, so the client never emits an offset-less value:
`DateTimeKind.Utc` is emitted as-is with `Z`; `DateTimeKind.Local` (e.g. `DateTime.Now`) is
converted to its UTC instant via `ToUniversalTime()`; `DateTimeKind.Unspecified` is treated as
UTC. If your `Unspecified` values represent local wall-clock time, convert them yourself (or
use `DateTimeOffset`, which always carries its own offset and passes through unchanged).
