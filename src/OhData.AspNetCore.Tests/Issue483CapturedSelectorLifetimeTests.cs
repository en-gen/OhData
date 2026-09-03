using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// #483 — the compiled-delegate caches (s_etagCache / s_keyToStringCache /
// s_keyToUrlCache) are keyed by GetType() and store a delegate compiled from the
// FIRST-constructed instance's expressions. That instance is the startup-scope
// profile, whose scope is disposed immediately after registration
// (OhDataBuilder / OhDataRegistration build). A selector that closes over an
// injected dependency therefore froze the startup scope's instance into every
// later request, for the process lifetime, silently.
//
// Profiles are registered AddScoped SPECIFICALLY so they can inject scoped
// services, so the framework invites exactly the constructor shape that makes
// such a selector natural to write.
// ═══════════════════════════════════════════════════════════════════════════════

public class CapDoc
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// A scoped dependency with both failure modes in one type: it throws once its scope is
/// disposed (the loud half), and each instance carries a distinct id (the silent half —
/// stale reuse of another scope's instance, which no exception ever reveals).
/// </summary>
public sealed class ScopedStamp : IDisposable
{
    private static int s_next;
    private bool _disposed;

    public int InstanceId { get; } = Interlocked.Increment(ref s_next);

    public string Format(string input)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ScopedStamp));
        return InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + input;
    }

    public void Dispose() => _disposed = true;
}

/// <summary>Same shape for a key selector: the base-constructor lambda captures the ctor
/// parameter, which is the only way an <c>EntitySetProfile</c> key selector can close over
/// injected state (a field cannot be referenced in a base initialiser).</summary>
public sealed class ScopedKey
{
    private static int s_next;
    public int Value { get; } = Interlocked.Increment(ref s_next);
}

public sealed class CapturingEtagProfile : EntitySetProfile<int, CapDoc>
{
    private readonly ScopedStamp _stamp;

    public CapturingEtagProfile(ScopedStamp stamp) : base(x => x.Id)
    {
        _stamp = stamp;
        EntitySetName = "CapturingEtagDocs";
        GetById = (id, ct) => OhDataResult.SuccessTask<CapDoc>(new CapDoc { Id = id, Name = "n" });
        // The selector closes over `this`, hence over the scoped dependency assigned above.
        UseETag(x => _stamp.Format(x.Name));
    }
}

public sealed class PlainEtagProfile : EntitySetProfile<int, CapDoc>
{
    public PlainEtagProfile() : base(x => x.Id)
    {
        EntitySetName = "PlainEtagDocs";
        GetById = (id, ct) => OhDataResult.SuccessTask<CapDoc>(new CapDoc { Id = id, Name = "n" });
        UseETag(x => x.Name);
    }
}

public sealed class CapturingKeyProfile : EntitySetProfile<int, CapDoc>
{
    // Degenerate as a key selector — but it is the ONE capturing shape the constructor's
    // "direct property access" check admits (a member access rooted at the captured object
    // is still a MemberExpression), so it is the shape the key caches must be safe against.
    public CapturingKeyProfile(ScopedKey key) : base(x => key.Value)
    {
        EntitySetName = "CapturingKeyDocs";
        GetById = (id, ct) => OhDataResult.SuccessTask<CapDoc>(new CapDoc { Id = id, Name = "n" });
    }
}

public class Issue483CapturedSelectorLifetimeTests
{
    private static ServiceProvider BuildProvider(params Type[] profileTypes)
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedStamp>();
        services.AddScoped<ScopedKey>();
        foreach (Type t in profileTypes) services.AddScoped(t);
        return services.BuildServiceProvider();
    }

    private static Func<TModel, string>? ReadCompiledETag<TKey, TModel>(EntitySetProfile<TKey, TModel> profile)
        where TModel : class =>
        (Func<TModel, string>?)typeof(EntitySetProfile<TKey, TModel>)
            .GetField("_getETag", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(profile);

    /// <summary>
    /// The loud half, made deterministic: resolve through a scope, dispose that scope, then
    /// resolve and invoke through a live one. No race, no timing — the first scope is provably
    /// gone before the second is used.
    /// </summary>
    [Fact]
    public void EtagSelectorCapturingAScopedDependency_DoesNotUseTheDisposedStartupInstance()
    {
        using ServiceProvider sp = BuildProvider(typeof(CapturingEtagProfile));

        // The "startup scope": constructs the profile once, then goes away.
        using (IServiceScope startup = sp.CreateScope())
        {
            startup.ServiceProvider.GetRequiredService<CapturingEtagProfile>();
        }

        using IServiceScope request = sp.CreateScope();
        var profile = (IEntitySetEndpointSource)request.ServiceProvider
            .GetRequiredService<CapturingEtagProfile>();

        // Pre-fix: ObjectDisposedException — the cached delegate holds the disposed instance.
        string etag = profile.InvokeGetETag(new CapDoc { Id = 1, Name = "n" });
        Assert.False(string.IsNullOrEmpty(etag));
    }

    /// <summary>
    /// The silent half — the one no exception reveals. Two LIVE scopes hold two different
    /// <see cref="ScopedStamp"/> instances, so the same model must hash differently in each.
    /// Pre-fix both scopes ran the first scope's stamp and produced identical ETags.
    /// </summary>
    [Fact]
    public void EtagSelectorCapturingAScopedDependency_DoesNotFreezeTheFirstScopesInstance()
    {
        using ServiceProvider sp = BuildProvider(typeof(CapturingEtagProfile));
        var model = new CapDoc { Id = 1, Name = "n" };

        using IServiceScope a = sp.CreateScope();
        using IServiceScope b = sp.CreateScope();
        var pa = (IEntitySetEndpointSource)a.ServiceProvider.GetRequiredService<CapturingEtagProfile>();
        var pb = (IEntitySetEndpointSource)b.ServiceProvider.GetRequiredService<CapturingEtagProfile>();

        Assert.NotSame(
            a.ServiceProvider.GetRequiredService<ScopedStamp>(),
            b.ServiceProvider.GetRequiredService<ScopedStamp>());
        Assert.NotEqual(pa.InvokeGetETag(model), pb.InvokeGetETag(model));
    }

    /// <summary>The key selector's caches (<c>s_keyToStringCache</c>/<c>s_keyToUrlCache</c>) have
    /// exactly the same shape and get exactly the same treatment.</summary>
    [Fact]
    public void KeySelectorCapturingAScopedDependency_IsNotFrozenAcrossScopes()
    {
        using ServiceProvider sp = BuildProvider(typeof(CapturingKeyProfile));
        var model = new CapDoc { Id = 1, Name = "n" };

        using IServiceScope a = sp.CreateScope();
        using IServiceScope b = sp.CreateScope();
        var pa = (IEntitySetEndpointSource)a.ServiceProvider.GetRequiredService<CapturingKeyProfile>();
        var pb = (IEntitySetEndpointSource)b.ServiceProvider.GetRequiredService<CapturingKeyProfile>();

        Assert.NotEqual(pa.InvokeGetKeyString(model), pb.InvokeGetKeyString(model));
        Assert.NotEqual(pa.InvokeGetKeyForUrl(model), pb.InvokeGetKeyForUrl(model));
    }

    /// <summary>
    /// The other direction: the cache must still do its job. A selector that touches nothing but
    /// the model parameter is compiled once for the process and shared by reference across
    /// instances — so this fix costs the ordinary profile nothing.
    /// </summary>
    [Fact]
    public void NonCapturingEtagSelector_StillSharesOneCompiledDelegate()
    {
        using ServiceProvider sp = BuildProvider(typeof(PlainEtagProfile));
        using IServiceScope a = sp.CreateScope();
        using IServiceScope b = sp.CreateScope();

        var pa = a.ServiceProvider.GetRequiredService<PlainEtagProfile>();
        var pb = b.ServiceProvider.GetRequiredService<PlainEtagProfile>();

        Assert.NotSame(pa, pb);
        Assert.Same(ReadCompiledETag(pa), ReadCompiledETag(pb));
    }

    /// <summary>The structural counterpart of the test above: a capturing selector must NOT be
    /// shared, which is the whole mechanism. Asserted on the delegate reference rather than on a
    /// symptom, so a future refactor that reintroduces sharing fails here first.</summary>
    [Fact]
    public void CapturingEtagSelector_IsCompiledPerInstance()
    {
        using ServiceProvider sp = BuildProvider(typeof(CapturingEtagProfile));
        using IServiceScope a = sp.CreateScope();
        using IServiceScope b = sp.CreateScope();

        var pa = a.ServiceProvider.GetRequiredService<CapturingEtagProfile>();
        var pb = b.ServiceProvider.GetRequiredService<CapturingEtagProfile>();

        Assert.NotSame(ReadCompiledETag(pa), ReadCompiledETag(pb));
    }
}
