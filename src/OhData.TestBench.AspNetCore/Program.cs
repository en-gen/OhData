using Microsoft.EntityFrameworkCore;
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

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "v1");
    c.SwaggerEndpoint("/swagger/v2/swagger.json", "v2");
});

// Scalar API reference at /scalar
app.MapScalarApiReference();

app.MapOhData("v1").WithOpenApi().WithGroupName("v1");
app.MapOhData("v2").WithOpenApi().WithGroupName("v2");

// Redirect root to Scalar
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();

app.Run();
