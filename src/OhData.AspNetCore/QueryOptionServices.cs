using System;
using Microsoft.AspNetCore.OData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OhData;

/// <summary>
/// Request services as <c>ODataQueryOptions</c>' constructor sees them: identical to the host's,
/// except that <c>IOptions&lt;ODataOptions&gt;</c> reports the optional-<c>$</c> scheme as OFF.
/// OhData declares OData 4.0, under which a key without <c>$</c> is a custom query option (Part 2
/// §5.2) and is never applied. Scoped to the construction rather than set on the host's own
/// <c>ODataOptions</c>, so a host that also runs <c>Microsoft.AspNetCore.OData</c> controllers
/// keeps its own setting (#714).
/// </summary>
internal sealed class DollarRequiredServiceProvider : IServiceProvider, ISupportRequiredService
{
    private static readonly IOptions<ODataOptions> s_options =
        Options.Create(new ODataOptions { EnableNoDollarQueryOptions = false });

    private readonly IServiceProvider _inner;

    internal DollarRequiredServiceProvider(IServiceProvider inner) => _inner = inner;

    public object? GetService(Type serviceType)
        => serviceType == typeof(IOptions<ODataOptions>) ? s_options : _inner.GetService(serviceType);

    public object GetRequiredService(Type serviceType)
        => serviceType == typeof(IOptions<ODataOptions>) ? s_options : _inner.GetRequiredService(serviceType);
}
