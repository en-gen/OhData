namespace OhData.Abstractions;

/// <summary>
/// Controls the midpoint-rounding behavior of the OData <c>round()</c> canonical function
/// (Part 2 §5.1.1.9) on the <c>GetQueryable</c> pushdown path.
/// </summary>
public enum RoundingMode
{
    /// <summary>
    /// Round-half-away-from-zero, per OData Part 2 §5.1.1.9 (e.g. <c>2.5 → 3</c>,
    /// <c>-2.5 → -3</c>). This is the framework default.
    /// <para>
    /// Microsoft.OData's <c>ApplyTo</c> binder emits .NET's single-argument
    /// <c>Math.Round(double)</c>/<c>Math.Round(decimal)</c>, which defaults to banker's rounding
    /// (round-half-to-even) and deviates from the spec on exact midpoints. OhData rewrites those
    /// calls to the two-argument <c>Math.Round(value, MidpointRounding.AwayFromZero)</c> overload
    /// before the query is enumerated.
    /// </para>
    /// <para>
    /// <b>Caveat:</b> the two-argument overload is not translatable by every EF Core provider.
    /// If a query using <c>round()</c> throws a translation error against your provider, set
    /// <see cref="BankersRounding"/> on the affected profile (or globally via
    /// <c>EntitySetDefaults.RoundingMode</c>) to fall back to the single-argument overload.
    /// </para>
    /// </summary>
    SpecCompliant = 0,

    /// <summary>
    /// Round-half-to-even ("banker's rounding"), .NET's <c>Math.Round</c> default (e.g.
    /// <c>2.5 → 2</c>). This deviates from OData Part 2 §5.1.1.9, which specifies
    /// round-half-away-from-zero. Opt into this mode when your EF Core provider cannot translate
    /// the <c>Math.Round(value, MidpointRounding.AwayFromZero)</c> overload that
    /// <see cref="SpecCompliant"/> requires.
    /// </summary>
    BankersRounding = 1,
}
