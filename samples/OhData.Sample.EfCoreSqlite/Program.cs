using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OhData.AspNetCore;
using OhData.Sample.EfCoreSqlite;

var builder = WebApplication.CreateBuilder(args);

// ── EF Core SQLite ───────────────────────────────────────────────────────────
// A real relational provider: the app.db file is created on first run by the committed
// migrations (see Database.Migrate() below). SQL statements are logged to the console via
// the "Microsoft.EntityFrameworkCore.Database.Command" logger category configured in
// appsettings.json — run an OData query and watch the translated SQL appear.
builder.Services.AddDbContext<ShopDbContext>(o => o.UseSqlite(
    builder.Configuration.GetConnectionString("Shop") ?? "Data Source=app.db"));

// ── OhData ───────────────────────────────────────────────────────────────────
// Profiles are registered scoped, so they can take the scoped ShopDbContext in their
// constructors — one DbContext per request, the normal ASP.NET Core lifetime.
builder.Services.AddOhData(o => o
    .WithPrefix("/odata")
    .AddEntitySetProfile<ProductProfile>()
    .AddEntitySetProfile<CategoryProfile>()
    .AddEntitySetProfile<TagProfile>()
    .AddEntitySetProfile<ProductSummaryProfile>());

var app = builder.Build();

// Apply the committed EF Core migrations (creates app.db on first run), then seed if empty.
using (IServiceScope scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
    db.Database.Migrate();
    SeedData.EnsureSeeded(db);
}

app.MapOhData();

app.Run();
