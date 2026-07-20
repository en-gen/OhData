using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

public class OhDataBuilderTests
{
    [Fact]
    public void AddOhData_RegistersProfileAndCreatesRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o.AddEntitySetProfile<WidgetProfile>());

        var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<OhDataRegistration>();

        Assert.Single(registration.EntitySetNames);
        Assert.Contains("Widgets", registration.EntitySetNames);
    }

    [Fact]
    public void AddOhData_DefaultPrefix_IsOdata()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o.AddEntitySetProfile<WidgetProfile>());

        var registration = services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();

        Assert.Equal("/odata", registration.Prefix);
    }

    [Fact]
    public void AddOhData_WithCustomPrefix_UsesPrefix()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o.WithPrefix("/api/v2").AddEntitySetProfile<WidgetProfile>());

        var registration = services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();

        Assert.Equal("/api/v2", registration.Prefix);
    }

    [Fact]
    public void AddOhData_MultipleProfiles_AllRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o
            .AddEntitySetProfile<WidgetProfile>()
            .AddEntitySetProfile<EmptyProfile>());

        var registration = services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();

        Assert.Equal(2, registration.EntitySetNames.Count());
    }

    [Fact]
    public void AddOhData_BuildsEdmModel()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o.AddEntitySetProfile<WidgetProfile>());

        var registration = services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();

        Assert.NotNull(registration.EdmModel);
    }

    [Fact]
    public void WithPrefix_TrailingSlash_IsStripped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o.WithPrefix("/api/").AddEntitySetProfile<WidgetProfile>());

        var registration = services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();

        Assert.Equal("/api", registration.Prefix);
    }

    [Fact]
    public void Startup_DuplicateEntitySetName_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOhData(o => o
                .AddEntitySetProfile<WidgetProfile>()
                .AddEntitySetProfile<WidgetProfile>()); // same name twice
            services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();
        });
    }

    [Fact]
    public void AddEntitySetProfile_Duplicate_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddOhData(o =>
            {
                o.AddEntitySetProfile<WidgetProfile>();
                o.AddEntitySetProfile<WidgetProfile>(); // duplicate — should throw
            }));
    }

    [Fact]
    public void AddOhData_Named_BothRegistrationsAccessible()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData("v1", o => o.WithPrefix("/v1").AddEntitySetProfile<WidgetProfile>());
        services.AddOhData("v2", o => o.WithPrefix("/v2").AddEntitySetProfile<EmptyProfile>());
        var provider = services.BuildServiceProvider();
        var v1 = provider.GetRequiredKeyedService<OhDataRegistration>("v1");
        var v2 = provider.GetRequiredKeyedService<OhDataRegistration>("v2");
        Assert.Equal("/v1", v1.Prefix);
        Assert.Equal("/v2", v2.Prefix);
    }

    // ── S5: startup validation — unbound operation route collisions ────────────────

    private static Task<string> Echo(string name) => Task.FromResult(name);
    private static Task<int> Add(int a, int b) => Task.FromResult(a + b);

    [Fact]
    public void Startup_UnboundFunction_CollidesWithEntitySetName_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o
            .AddEntitySetProfile<WidgetProfile>() // EntitySetName = "Widgets", has GetAll (GET /Widgets)
            .AddFunction((Func<string, Task<string>>)Echo, "Widgets")); // also GET /Widgets

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.BuildServiceProvider().GetRequiredService<OhDataRegistration>());
        Assert.Contains("Widgets", ex.Message, StringComparison.Ordinal);
        Assert.Contains("function", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Startup_UnboundAction_CollidesWithEntitySetName_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o
            .AddEntitySetProfile<WidgetProfile>() // EntitySetName = "Widgets", has Post (POST /Widgets)
            .AddAction((Func<int, int, Task<int>>)Add, "Widgets")); // also POST /Widgets

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.BuildServiceProvider().GetRequiredService<OhDataRegistration>());
        Assert.Contains("Widgets", ex.Message, StringComparison.Ordinal);
        Assert.Contains("action", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Startup_UnboundFunction_NoCollisionWithEntitySet_NoGetAll_DoesNotThrow()
    {
        // EmptyProfile has no GetAll/GetQueryable, so GET /EmptyWidgets is never registered —
        // an unbound function of the same name is not a genuine route collision.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o
            .AddEntitySetProfile<EmptyProfile>() // EntitySetName = "EmptyWidgets"
            .AddFunction((Func<string, Task<string>>)Echo, "EmptyWidgets"));

        var registration = services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();
        Assert.Single(registration.UnboundOperations);
    }

    [Fact]
    public void Startup_DuplicateUnboundFunctionName_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o
            .AddEntitySetProfile<WidgetProfile>()
            .AddFunction((Func<string, Task<string>>)Echo, "Greet")
            .AddFunction((Func<string, Task<string>>)Echo, "Greet")); // duplicate function name

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.BuildServiceProvider().GetRequiredService<OhDataRegistration>());
        Assert.Contains("Greet", ex.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Startup_DuplicateUnboundActionName_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o
            .AddEntitySetProfile<WidgetProfile>()
            .AddAction((Func<int, int, Task<int>>)Add, "Sum")
            .AddAction((Func<int, int, Task<int>>)Add, "Sum")); // duplicate action name

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.BuildServiceProvider().GetRequiredService<OhDataRegistration>());
        Assert.Contains("Sum", ex.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Startup_UnboundFunctionAndAction_SameName_DoesNotThrow()
    {
        // Different HTTP methods (GET vs. POST) -- not a genuine route collision.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o
            .AddEntitySetProfile<EmptyProfile>()
            .AddFunction((Func<string, Task<string>>)Echo, "Widgets")
            .AddAction((Func<int, int, Task<int>>)Add, "Widgets"));

        var registration = services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();
        Assert.Equal(2, registration.UnboundOperations.Count);
    }
}
