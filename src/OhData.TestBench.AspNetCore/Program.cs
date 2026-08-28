using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using OhData.AspNetCore.Swashbuckle;
using OhData.TestBench.AspNetCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ── EF Core InMemory (scoped — the EF Core default and what OhData profiles expect) ──
// DbContext is not thread-safe and its change tracker must not be shared across
// requests: a singleton registration means one failed SaveChanges() leaves a poisoned
// entity in the tracker forever, bricking every subsequent write for the process
// lifetime (#356). Profiles are registered AddScoped specifically so they can inject
// a scoped DbContext (see CLAUDE.md), so this must be scoped too — the InMemory
// provider keeps all scopes pointed at the same named database ("TestBench"), so data
// still persists across requests exactly as before.
builder.Services.AddDbContext<AppDbContext>(
    o => o.UseInMemoryDatabase("TestBench"));

// ── OpenAPI / Swagger ─────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "OhData TestBench — Movies (v1)", Version = "v1" });
    c.SwaggerDoc("v2", new() { Title = "OhData TestBench — Movies (v2)", Version = "v2" });
    // Route each endpoint to the doc matching its group name
    c.DocInclusionPredicate((docName, apiDesc) =>
        apiDesc.GroupName is null || apiDesc.GroupName == docName);
    // One-line canonical registration: wires both the OData query-parameter operation filter and
    // the schema-fidelity filter (#228 — a no-op while no TestBench profile ignores anything, but
    // registered here to demonstrate the recommended setup).
    c.AddOhData();
});

// ── OhData versioned registrations ───────────────────────────────────────────
//
// v1: Movies + Genres           -- the simple surface: GetQueryable CRUD + ETags + bound
//                                   operations on Movies, GetAll on Genres. AllowDeepWrites
//                                   stays at its default (false), and Movie.Cast/Studio have no
//                                   navigation handlers -- see MovieProfile's comments.
// v2: Movies + Genres + Actors + Studios -- adds deep insert, batch-loaded $expand, and $ref
//                                   link management on Movie's navigations, plus the Actor and
//                                   Studio entity sets those navigations point at.
//
builder.Services.AddOhDataVersion("v1", "/v1", o =>
    o.AddEntitySetProfile<MovieProfile>()
     .AddEntitySetProfile<GenreProfile>());

builder.Services.AddOhDataVersion("v2", "/v2", o =>
    o.AddEntitySetProfile<MovieProfileV2>()
     .AddEntitySetProfile<GenreProfileV2>()
     .AddEntitySetProfile<ActorProfile>()
     .AddEntitySetProfile<StudioProfile>());

// ── App pipeline ──────────────────────────────────────────────────────────────
var app = builder.Build();

// Seed the in-memory database. AppDbContext is now scoped (#356), so it must be resolved
// from an explicit scope rather than the root service provider -- resolving a scoped
// service directly from app.Services would throw once scope validation is enabled.
using (var seedScope = app.Services.CreateScope())
{
    DbSeeder.Seed(seedScope.ServiceProvider.GetRequiredService<AppDbContext>());
}

// Support reverse proxies (Render, Azure, etc.) forwarding scheme/host headers
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// Expose OpenAPI JSON at /openapi/{documentName}.json — Scalar's expected default
app.UseSwagger(c => c.RouteTemplate = "/openapi/{documentName}.json");
app.UseSwaggerUI(c =>
{
    // v2 (the full surface: Movies + Genres + Actors + Studios, with $expand/$ref) is listed
    // first so it is the document Swagger UI selects by default. v1 remains available in the
    // top-right doc dropdown as the deliberately-simpler contrast.
    c.SwaggerEndpoint("/openapi/v2.json", "v2");
    c.SwaggerEndpoint("/openapi/v1.json", "v1");
});

// Scalar API reference at /scalar/{documentName} — uses /openapi/{documentName}.json by default
app.MapScalarApiReference();

app.MapOhData("v1").WithGroupName("v1");
app.MapOhData("v2").WithGroupName("v2");

// Redirect root to the Scalar v2 doc -- v2 is the full showcase (Actors, Studios, $expand,
// $ref); visitors land there by default. /scalar/v1 stays reachable directly.
app.MapGet("/", () => Results.Redirect("/scalar/v2")).ExcludeFromDescription();

app.MapGet("/health", () => Results.Ok()).ExcludeFromDescription();

app.Run();

// Testability marker (#356): makes the implicit top-level Program class public so
// WebApplicationFactory<Program> can boot this exact app -- unchanged, unreconfigured -- from
// OhData.TestBench.AspNetCore.Tests to assert AppDbContext's registered lifetime and exercise
// the real POST/PATCH/DELETE pipeline end-to-end. No behavioral effect on the running app.
public partial class Program { }
