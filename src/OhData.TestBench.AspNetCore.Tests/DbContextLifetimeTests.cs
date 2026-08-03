using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OhData.TestBench.AspNetCore;
using Xunit;

namespace OhData.TestBench.AspNetCore.Tests;

/// <summary>
/// #356: <c>OhData.TestBench.AspNetCore/Program.cs</c> registered <see cref="AppDbContext"/> as
/// a singleton -- a well-known EF Core anti-pattern that leaves the change tracker permanently
/// poisoned after the first failed <c>SaveChanges()</c>, bricking every subsequent write for the
/// life of the process. The fix is registering it scoped (the <c>AddDbContext</c> default). Both
/// halves of that fix are covered here:
/// <list type="bullet">
/// <item><see cref="AppDbContext_IsRegisteredScoped"/> -- a direct DI-registration assertion,
/// the most precise thing to check since #356 IS specifically about the registered
/// <see cref="ServiceLifetime"/>.</item>
/// <item><see cref="FailedWrite_DoesNotBrickSubsequentWrites"/> -- an end-to-end reproduction of
/// the exact scenario the issue reported (steps 1-3 of its Evidence section): a write that fails
/// must not take down every write that follows it on the same process.</item>
/// </list>
/// Each test spins up its own <see cref="WebApplicationFactory{TEntryPoint}"/> instance against
/// the REAL <c>Program.cs</c> (unmodified apart from the public partial-class testability
/// marker), with the InMemory database name replaced by a fresh GUID per instance so the two
/// tests -- and any other test that boots this factory -- never share state through EF Core's
/// process-wide InMemory database registry.
/// </summary>
public class DbContextLifetimeTests
{
    private static WebApplicationFactory<Program> CreateFactory()
    {
        string dbName = "TestBench-" + Guid.NewGuid();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace the app's own AddDbContext<AppDbContext>(...) registration with an
                // identically-scoped one pointed at a private, per-test-instance InMemory
                // database, so tests never see each other's writes (or the app's default
                // "TestBench" seed data mutated by a previous test run in the same process).
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
            });
        });
    }

    [Fact]
    public void AppDbContext_IsRegisteredScoped()
    {
        using var factory = CreateFactory();
        // Force the host to build so ConfigureServices above has actually run and the final
        // service collection is queryable via the host's IServiceProvider descriptors. The
        // cleanest way to inspect the registered ServiceLifetime without touching internals is
        // to capture the IServiceCollection during ConfigureServices itself.
        ServiceLifetime? capturedLifetime = null;
        using var inspectingFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(AppDbContext));
                capturedLifetime = descriptor?.Lifetime;
            });
        });
        using var client = inspectingFactory.CreateClient();

        Assert.NotNull(capturedLifetime);
        Assert.Equal(ServiceLifetime.Scoped, capturedLifetime);
    }

    [Fact]
    public async Task FailedWrite_DoesNotBrickSubsequentWrites()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // 1) A wholly valid POST succeeds.
        var first = await client.PostAsJsonAsync("/v1/Movies", new
        {
            Title = "Valid One",
            Year = 2000,
            Rating = 5m,
            RatingCount = 1,
            RuntimeMinutes = 100,
            GenreCode = "DRAMA",
            StudioId = 1,
            ReleaseDate = "2000-01-01",
        });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // 2) A POST with a null Title violates AppDbContext's non-nullable-property convention
        // (EF Core InMemory enforces "required" on non-nullable CLR properties) and fails
        // SaveChanges with a DbUpdateException the framework does not specially handle --
        // surfacing as 500, exactly as the issue's Evidence section describes. This is the
        // known, separately-tracked "request body not validated against EDM" defect; #356 is
        // NOT about fixing this 500, only about what happens to the DbContext afterward.
        var poison = await client.PostAsJsonAsync("/v1/Movies", new
        {
            Title = (string?)null,
            Year = 2001,
            Rating = 5m,
            RatingCount = 1,
            RuntimeMinutes = 100,
            GenreCode = "DRAMA",
            StudioId = 1,
            ReleaseDate = "2001-01-01",
        });
        Assert.Equal(HttpStatusCode.InternalServerError, poison.StatusCode);

        // 3) On the pre-#356 singleton registration, the poisoned entity stays stuck in the
        // ONE shared change tracker's Added state forever, so this wholly unrelated, entirely
        // valid POST would ALSO fail with 500 (and every write after it, forever). With
        // AppDbContext registered scoped, this request gets a fresh DbContext/change tracker
        // and must succeed.
        var third = await client.PostAsJsonAsync("/v1/Movies", new
        {
            Title = "Valid Two",
            Year = 2002,
            Rating = 5m,
            RatingCount = 1,
            RuntimeMinutes = 100,
            GenreCode = "DRAMA",
            StudioId = 1,
            ReleaseDate = "2002-01-01",
        });
        Assert.Equal(HttpStatusCode.Created, third.StatusCode);

        // 4) PATCH/DELETE on the process are likewise unaffected by the earlier failure.
        var patch = await client.PatchAsync(
            "/v1/Movies(1)",
            JsonContent.Create(new { Year = 1999 }));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
    }
}
