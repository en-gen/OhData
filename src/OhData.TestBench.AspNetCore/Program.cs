using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData.AspNetCore;
using OhData.AspNetCore.Versioning;
using OhData.TestBench.AspNetCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ── EF Core InMemory (singleton for demo; profiles are also singletons) ──────
builder.Services.AddDbContext<AppDbContext>(
    o => o.UseInMemoryDatabase("TestBench"),
    ServiceLifetime.Singleton);

// ── OpenAPI / Swagger ─────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "OhData TestBench — v1", Version = "v1" });
    c.SwaggerDoc("v2", new() { Title = "OhData TestBench — v2", Version = "v2" });
    // Route each endpoint to the doc matching its group name
    c.DocInclusionPredicate((docName, apiDesc) =>
        apiDesc.GroupName is null || apiDesc.GroupName == docName);
});

// ── OhData versioned registrations ───────────────────────────────────────────
//
// v1: Products + Categories
// v2: Products + Orders (with navigation routing for order lines) + Categories
//
builder.Services.AddOhDataVersion("v1", "/v1", o =>
    o.AddProfile<ProductProfile>()
     .AddProfile<CategoryProfile>());

builder.Services.AddOhDataVersion("v2", "/v2", o =>
    o.AddProfile<ProductProfile>()
     .AddProfile<OrderProfile>()
     .AddProfile<CategoryProfile>());

// ── App pipeline ──────────────────────────────────────────────────────────────
var app = builder.Build();

// Seed the in-memory database
DbSeeder.Seed(app.Services.GetRequiredService<AppDbContext>());

// Support reverse proxies (Render, Azure, etc.) forwarding scheme/host headers
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// Expose OpenAPI JSON at /openapi/{documentName}.json — Scalar's expected default
app.UseSwagger(c => c.RouteTemplate = "/openapi/{documentName}.json");
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/openapi/v1.json", "v1");
    c.SwaggerEndpoint("/openapi/v2.json", "v2");
});

// Scalar API reference at /scalar/{documentName} — uses /openapi/{documentName}.json by default
app.MapScalarApiReference();

app.MapOhData("v1").WithGroupName("v1");
app.MapOhData("v2").WithGroupName("v2");

// Redirect root to Scalar v1 doc
app.MapGet("/", () => Results.Redirect("/scalar/v1")).ExcludeFromDescription();

app.MapGet("/health", () => Results.Ok()).ExcludeFromDescription();

app.Run();
