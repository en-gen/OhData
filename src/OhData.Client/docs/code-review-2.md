# Code Review 2 — Filter Expression Translator Robustness

## Summary

Deep review of `FilterTranslator.cs` focusing on: supported node types, unrecognised node error messages, parenthesization for AND/OR precedence, string quoting/escaping, and DateTime/DateTimeOffset handling. The translator is generally solid but has several gaps around arithmetic expression parenthesization, `string.IsNullOrEmpty` visiting the argument expression twice (compiling it twice as a closure), DateTime literal format compliance with the OData spec, and missing support for `string.IsNullOrWhiteSpace`.

## Findings

### FINDING-2-1: `string.IsNullOrEmpty` visits the argument expression twice — compiles closure twice [severity: major]

**Current code:**
```csharp
if (node.Method.Name     == "IsNullOrEmpty"
 && node.Method.IsStatic
 && node.Method.DeclaringType == typeof(string))
{
    _sb.Append('(');
    Visit(node.Arguments[0]);         // visits once
    _sb.Append(" eq null or ");
    Visit(node.Arguments[0]);         // visits a SECOND time
    _sb.Append(" eq '')");
    return node;
}
```

`node.Arguments[0]` is visited (and any captured-variable closures are compiled) twice. For a simple property access this is harmless. For a captured-variable member expression, `VisitMember` calls `Expression.Lambda<Func<object?>>(...).Compile()()` each time. This doubles the compilation cost and is semantically redundant.

**Thought tree:**
- Option A: Capture the argument as a local and visit it twice — cannot avoid double `_sb.Append`.
  - Pro: At least documents intentional duplication.
  - Con: Still double-compiles closures.
- Option B: Use a helper that emits the argument path/literal once and appends it via string, then reuses the string.
  - Pro: No double compile; deterministic.
  - Con: Requires extracting the sub-expression to a string, which the visitor does not currently support.
- Option C: Save and restore the `StringBuilder` position, render once, store the substring, then emit twice.
  - Pro: Works with the visitor pattern; no double compile.
  - Con: Requires tracking position offsets which is brittle.

**Decision:** Option C — save `_sb.Length` before visiting, extract the rendered sub-expression, then append it a second time as a raw string (no re-visit needed).

**Proposed fix:**
```csharp
if (node.Method.Name == "IsNullOrEmpty"
 && node.Method.IsStatic
 && node.Method.DeclaringType == typeof(string))
{
    var start = _sb.Length;
    Visit(node.Arguments[0]);
    var rendered = _sb.ToString(start, _sb.Length - start);
    _sb.Append($"({rendered} eq null or {rendered} eq '')");
    return node;
}
```

---

### FINDING-2-2: DateTime literal format is not OData-spec-compliant — quotes are wrong [severity: critical]

**Current code:**
```csharp
DateTime dt     => dt.Kind == DateTimeKind.Utc
                       ? $"'{dt:yyyy-MM-ddTHH:mm:ssZ}'"
                       : $"'{dt:yyyy-MM-ddTHH:mm:ss}'",
DateTimeOffset dto => dto.Offset == TimeSpan.Zero
                       ? $"'{dto:yyyy-MM-ddTHH:mm:ssZ}'"
                       : $"'{dto:yyyy-MM-ddTHH:mm:sszzz}'",
```

OData 4.0 spec (ABNF, section 5.1.1.6) specifies that `Edm.DateTimeOffset` literals in `$filter` are **not** quoted with single-quotes. They are bare ISO 8601 strings: `DateTimeOffset eq 2024-06-01T12:00:00Z`. The current code wraps them in single quotes, which will cause OData servers to reject the filter with a type mismatch or parse error.

Similarly, `DateTime` maps to `Edm.DateTimeOffset` in OData 4.0 (there is no `Edm.DateTime`), so it must also use the unquoted form.

**Thought tree:**
- Option A: Remove the surrounding single quotes from DateTime/DateTimeOffset formats.
  - Pro: Correct per spec; interoperable with standard OData servers.
  - Con: Breaking change for any callers relying on the current (wrong) behaviour.
- Option B: Keep single quotes for compatibility with lenient servers.
  - Pro: Some servers accept quoted DateTime.
  - Con: Non-conforming; many servers will reject it.
- Option C: Add a flag to `OhDataClientOptions` to toggle the format.
  - Pro: Flexible.
  - Con: Over-engineered; the spec is clear.

**Decision:** Option A — remove single quotes. The spec is unambiguous.

**Proposed fix:**
```csharp
DateTime dt     => dt.Kind == DateTimeKind.Utc
                       ? dt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                       : dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
DateTimeOffset dto => dto.Offset == TimeSpan.Zero
                       ? dto.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                       : dto.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
```

---

### FINDING-2-3: Arithmetic operators lack parenthesization — operator-precedence ambiguity [severity: minor]

**Current code:**
```csharp
ExpressionType.Add      => ("add", false),
ExpressionType.Subtract => ("sub", false),
ExpressionType.Multiply => ("mul", false),
ExpressionType.Divide   => ("div", false),
ExpressionType.Modulo   => ("mod", false),
```

With `parens = false`, the expression `x.A + x.B * x.C` becomes `A add B mul C`. OData parsers follow standard arithmetic precedence (`mul`/`div` before `add`/`sub`), so the unparenthesized form is correct. However, in a complex expression like `(x.A + x.B) * x.C`, the expression tree correctly wraps the addition in a binary node with multiplication at the root, so the visitor would produce `(A add B) mul C`... but actually with `parens = false` it would produce `A add B mul C` — incorrect!

The issue: `parens = false` means no parentheses are added around either operand. So `(A + B) * C` (where the addition node is a child of the multiply node) would visit: multiply left=Add(A,B) → `A add B`, then ` mul `, then right=C → `C`, yielding `A add B mul C` which is wrong.

**Thought tree:**
- Option A: Always parenthesize arithmetic operations (set `parens = true` for all arithmetic).
  - Pro: Always correct; no precedence issues.
  - Con: Slightly verbose output for simple cases.
- Option B: Only parenthesize when a lower-precedence operator is nested inside a higher-precedence one.
  - Pro: Minimal parentheses.
  - Con: Complex logic; easy to get wrong.
- Option C: Set `parens = true` only for add/sub (since mul/div/mod have higher precedence).
  - Pro: Correct for the common case.
  - Con: Doesn't handle all nesting; e.g. `A / (B + C)`.

**Decision:** Option A — always parenthesize arithmetic operands. The output is slightly more verbose but always correct and unambiguous.

**Proposed fix:**
```csharp
ExpressionType.Add      => ("add", true),
ExpressionType.Subtract => ("sub", true),
ExpressionType.Multiply => ("mul", true),
ExpressionType.Divide   => ("div", true),
ExpressionType.Modulo   => ("mod", true),
```

---

### FINDING-2-4: `string.IsNullOrWhiteSpace` is not supported [severity: minor]

**Current code** does not handle `string.IsNullOrWhiteSpace`. A developer calling `.Filter(x => !string.IsNullOrWhiteSpace(x.Name))` gets a `NotSupportedException` with the generic "method not supported" message.

**Thought tree:**
- Option A: Support it by translating to `not (trim(prop) eq '')` (OData 4.0 does not have an `isNullOrWhiteSpace` function).
  - Pro: Useful; covers the common use case.
  - Con: Does not correctly handle null — `trim(null)` in OData is null, not `''`, so the condition `trim(prop) eq ''` is false when prop is null. Must combine with `prop eq null`.
- Option B: Throw a `NotSupportedException` with a helpful message explaining the OData translation.
  - Pro: Honest about the limitation; guides the developer.
  - Con: Still a runtime error.
- Option C: Translate to `(prop eq null or trim(prop) eq '')`.
  - Pro: Correct semantic equivalent.
  - Con: Verbose but accurate.

**Decision:** Option C — implement as `(prop eq null or trim(prop) eq '')` to match the C# semantics exactly.

**Proposed fix:**
```csharp
if (node.Method.Name == "IsNullOrWhiteSpace"
 && node.Method.IsStatic
 && node.Method.DeclaringType == typeof(string))
{
    var start = _sb.Length;
    Visit(node.Arguments[0]);
    var rendered = _sb.ToString(start, _sb.Length - start);
    _sb.Append($"({rendered} eq null or trim({rendered}) eq '')");
    return node;
}
```

---

## Changes made

1. **`FilterTranslator.cs`** — Fixed `IsNullOrEmpty` to use the position-snapshot approach to avoid double-compiling captured closures. Added `IsNullOrWhiteSpace` support. Changed arithmetic operators to use `parens = true` to avoid precedence ambiguity. Removed single-quote wrapping from `DateTime`/`DateTimeOffset` literals per OData 4.0 spec.

2. **`FilterTranslatorTests.cs`** — Updated `FormatLiteral_DateTimeUtc` test to expect unquoted form. Added new tests for `IsNullOrEmpty`, `IsNullOrWhiteSpace`, and arithmetic parenthesization.
