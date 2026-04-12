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
        services.AddOhData(o => o.AddProfile<WidgetProfile>());

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
        services.AddOhData(o => o.AddProfile<WidgetProfile>());

        var registration = services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();

        Assert.Equal("/odata", registration.Prefix);
    }

    [Fact]
    public void AddOhData_WithCustomPrefix_UsesPrefix()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o.WithPrefix("/api/v2").AddProfile<WidgetProfile>());

        var registration = services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();

        Assert.Equal("/api/v2", registration.Prefix);
    }

    [Fact]
    public void AddOhData_MultipleProfiles_AllRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o
            .AddProfile<WidgetProfile>()
            .AddProfile<EmptyProfile>());

        var registration = services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();

        Assert.Equal(2, registration.EntitySetNames.Count());
    }

    [Fact]
    public void AddOhData_BuildsEdmModel()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o.AddProfile<WidgetProfile>());

        var registration = services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();

        Assert.NotNull(registration.EdmModel);
    }

    [Fact]
    public void WithPrefix_TrailingSlash_IsStripped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o.WithPrefix("/api/").AddProfile<WidgetProfile>());

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
                .AddProfile<WidgetProfile>()
                .AddProfile<WidgetProfile>()); // same name twice
            services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();
        });
    }

    [Fact]
    public void AddProfile_Duplicate_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddOhData(o =>
            {
                o.AddProfile<WidgetProfile>();
                o.AddProfile<WidgetProfile>(); // duplicate — should throw
            }));
    }

    [Fact]
    public void AddOhData_Named_BothRegistrationsAccessible()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData("v1", o => o.WithPrefix("/v1").AddProfile<WidgetProfile>());
        services.AddOhData("v2", o => o.WithPrefix("/v2").AddProfile<EmptyProfile>());
        var provider = services.BuildServiceProvider();
        var v1 = provider.GetRequiredKeyedService<OhDataRegistration>("v1");
        var v2 = provider.GetRequiredKeyedService<OhDataRegistration>("v2");
        Assert.Equal("/v1", v1.Prefix);
        Assert.Equal("/v2", v2.Prefix);
    }

}
