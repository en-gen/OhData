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
/// <para>
/// <b>Harness note (deliberate divergence):</b> every other OhData test project uses
/// <c>Microsoft.AspNetCore.TestHost</c> via the shared in-repo <c>TestHostBuilder</c>, which
/// builds a fresh <c>WebApplicationBuilder</c> from scratch inside the test process -- it never
/// executes an existing app's own <c>Program.cs</c>. That pattern cannot exercise this bug: #356
/// is specifically about what <em>this app's actual startup code</em> registers, so the test
/// needs to boot the real <c>OhData.TestBench.AspNetCore/Program.cs</c> unmodified, which is
/// exactly what <c>Microsoft.AspNetCore.Mvc.Testing</c>'s <see cref="WebApplicationFactory{TEntryPoint}"/>
/// is built for (it locates and runs the target assembly's actual entry point). One satellite
/// test project per integration surface — this one paired 1:1 with the TestBench app it verifies
/// — is the repo's existing convention (see <c>OhData.Client.Tests</c> vs. <c>OhData.Client</c>,
/// etc.); referencing the sample app from the core <c>OhData.AspNetCore.Tests</c> project would
/// invert that and create a backward dependency from the framework's test suite onto a sample.
/// </para>
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
                // Point AppDbContext at a private, per-test-instance InMemory database, so tests
                // never see each other's writes (or the app's default "TestBench" seed data
                // mutated by a previous test run in the same process). Only
                // DbContextOptions<AppDbContext> is removed and re-registered here -- EF Core's
                // AddDbContext TryAdds the AppDbContext service descriptor itself, so if
                // Program.cs already registered one (it always has, by this point in host
                // construction), THIS call is a no-op for it and Program.cs's own registration
                // -- and its lifetime -- passes through untouched. That is what makes
                // AppDbContext_IsRegisteredScoped below a genuine assertion about Program.cs,
                // not this fixture.
                //
                // #356 review R3: the options re-registration must mirror THAT lifetime, not
                // default to Scoped. Read it BEFORE removing anything, so a Program.cs regressed
                // back to Singleton gets a Singleton-registered DbContextOptions<AppDbContext>
                // too. Doing this the naive way (removing the options and re-adding via the
                // parameterless-lifetime AddDbContext overload, which defaults to Scoped) mismatches
                // a Singleton AppDbContext against Scoped options -- ASP.NET Core's own DI scope
                // validation then fails host construction with "cannot consume scoped service
                // ... from singleton ..." BEFORE a single request runs, which is a real DI wiring
                // complaint but tells a maintainer nothing about #356's actual bug (a poisoned
                // change tracker bricking later, unrelated writes). Mirroring the lifetime lets
                // FailedWrite_DoesNotBrickSubsequentWrites reach that real bug on a regression.
                ServiceLifetime appLifetime = services
                    .LastOrDefault(d => d.ServiceType == typeof(AppDbContext))?.Lifetime
                    ?? ServiceLifetime.Scoped;

                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(
                    o => o.UseInMemoryDatabase(dbName),
                    contextLifetime: appLifetime,
                    optionsLifetime: appLifetime);
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

        // 2a) #355 CHANGED THIS STEP, and the change is the point. The original poison here was a
        // POST with a null Title, which reached the handler unvalidated and blew up SaveChanges --
        // the "request body not validated against EDM" defect this test used to describe as known
        // and separately tracked. It is tracked no longer: the framework now checks the body
        // against its own $metadata (Movie.Title is Nullable="false") and answers 400 before any
        // handler runs. Asserted here rather than merely dropped, because this test is where that
        // 500 was documented and a reader arriving from #355's Evidence section should find the
        // outcome, not a deleted step.
        var rejected = await client.PostAsJsonAsync("/v1/Movies", new
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
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        // 2b) #356 still needs a write that genuinely FAILS INSIDE the handler, since what it is
        // about is the state the DbContext is left in afterward. A duplicate primary key does it:
        // the body is a valid Movie by every published rule, so nothing the framework validates can
        // reject it, and `db.Movies.Add(...); db.SaveChanges();` throws on the existing key -- 500,
        // with the failed entity left Added in the change tracker. That is the same poisoning this
        // step always produced, sourced from a defect that is not scheduled to be fixed out from
        // under it.
        var poison = await client.PostAsJsonAsync("/v1/Movies", new
        {
            Id = 1,
            Title = "Duplicate Key",
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
