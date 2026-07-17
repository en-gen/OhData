using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace OhData.TestBench.AspNetCore;

// ── Entity models ─────────────────────────────────────────────────────────────
//
// The domain: a small movie catalog. Movie is the flagship queryable set (EF Core
// InMemory, full CRUD, ETags, bound operations, deep insert). Genre is a deliberately
// tiny static lookup used to showcase the GetAll (IEnumerable) path. Actor and Studio
// are simple queryable sets reached both directly (v2 only) and through Movie's
// navigation properties (Cast, Studio).

/// <summary>
/// The flagship entity set. Backed by EF Core InMemory via <see cref="AppDbContext"/> and
/// exposed through <c>GetQueryable</c> (see <see cref="MovieProfile"/>/<see cref="MovieProfileV2"/>)
/// so <c>$filter</c>/<c>$orderby</c>/<c>$skip</c>/<c>$top</c> push down to LINQ instead of being
/// applied after a full in-memory enumeration.
/// </summary>
public class Movie
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public int Year { get; set; }

    /// <summary>
    /// A synthetic 0-10 rating invented for this demo (NOT sourced from any real rating
    /// aggregator). Seeded once per movie, then folded into a running average by the
    /// <c>Rate</c> bound action -- see <see cref="MovieHandlers.Rate"/>.
    /// </summary>
    public decimal Rating { get; set; }

    /// <summary>Number of ratings folded into <see cref="Rating"/> so far (starts at 1, the seed value).</summary>
    public int RatingCount { get; set; }

    public int RuntimeMinutes { get; set; }

    /// <summary>Foreign key into <see cref="Genre"/> (string code, e.g. "SCIFI").</summary>
    public string GenreCode { get; set; } = "";

    /// <summary>Foreign key into <see cref="Studio"/>.</summary>
    public int StudioId { get; set; }

    public DateOnly ReleaseDate { get; set; }

    /// <summary>
    /// Bumped on every write (<c>Put</c>/<c>Patch</c>/<c>Rate</c>). This is the source
    /// value for <see cref="MovieProfile"/>'s <c>UseETag</c> call -- see the ETag teaching
    /// comment there for the If-Match flow this enables.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Collection navigation to the principal cast (many-to-many via <see cref="MovieActor"/>).
    /// Registered as a real EF relationship in <see cref="AppDbContext"/> so it can be queried
    /// efficiently by the navigation handlers in <see cref="MovieProfileV2"/>.
    /// </summary>
    public ICollection<Actor> Cast { get; set; } = new List<Actor>();

    /// <summary>Required single-valued navigation to the producing studio.</summary>
    public Studio Studio { get; set; } = null!;
}

/// <summary>
/// Join entity for the Movie&lt;-&gt;Actor many-to-many relationship. Not exposed as its own
/// OData entity set -- it exists purely so EF Core has somewhere to store the link rows that
/// back <see cref="Movie.Cast"/> and the <c>$ref</c> add/remove handlers in
/// <see cref="MovieProfileV2"/>.
/// </summary>
public class MovieActor
{
    public int MovieId { get; set; }
    public int ActorId { get; set; }
}

/// <summary>A movie's principal cast member. Queryable set, v2 only.</summary>
public class Actor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int BirthYear { get; set; }
}

/// <summary>The producing studio. Small queryable set, v2 only.</summary>
public class Studio
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Founded { get; set; }

    /// <summary>Reverse collection navigation, batch-loaded -- see <see cref="StudioProfile"/>.</summary>
    public ICollection<Movie> Movies { get; set; } = new List<Movie>();
}

/// <summary>
/// A small, static genre lookup (string key). Deliberately NOT backed by EF Core --
/// see <see cref="GenreProfile"/> for the GetAll-vs-GetQueryable teaching comment this exists for.
/// </summary>
public class Genre
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

// ── EF Core InMemory context ──────────────────────────────────────────────────

/// <summary>
/// Registered as a singleton for demo purposes so profiles (also singletons) can share it.
/// In production, use IDbContextFactory&lt;T&gt; to avoid scoped-in-singleton issues.
/// </summary>
/// <remarks>
/// <see cref="Movie.Cast"/>, <see cref="Movie.Studio"/>, and <see cref="Studio.Movies"/> are
/// deliberately NOT configured as real EF relationships below (they're <c>Ignore</c>d) even
/// though they exist as CLR navigation properties -- those properties only exist so the OData
/// profiles have something to point <c>HasMany</c>/<c>HasRequired</c> lambdas at
/// (<c>x =&gt; x.Cast</c>, <c>x =&gt; x.Studio</c>). All three navigations' actual data is loaded
/// by hand-written LINQ in <see cref="MovieProfileV2"/>/<see cref="StudioProfile"/>, never via
/// EF <c>.Include()</c> or navigation fixup. That matters specifically because this DbContext is
/// a long-lived singleton: if <c>Movie.Studio</c>/<c>Studio.Movies</c> WERE a real bidirectional
/// relationship, EF's automatic fixup would populate both sides on every tracked entity the
/// moment they're loaded together (no <c>Include()</c> needed for fixup to kick in) -- producing
/// a cyclic object graph (<c>Movie → Studio → Movies → Movie → ...</c>) that stack-overflows
/// System.Text.Json when a bare <c>GetQueryable</c> result gets serialized. Genuine two-way EF
/// relationships are safe in a normal (scoped, per-request) DbContext; they're a foot-gun here
/// only because of the singleton lifetime this demo uses for simplicity.
/// </remarks>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<Actor> Actors => Set<Actor>();
    public DbSet<Studio> Studios => Set<Studio>();
    public DbSet<MovieActor> MovieActors => Set<MovieActor>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Movie>().Ignore(m => m.Studio);
        modelBuilder.Entity<Movie>().Ignore(m => m.Cast);
        modelBuilder.Entity<Studio>().Ignore(s => s.Movies);

        modelBuilder.Entity<MovieActor>().HasKey(ma => new { ma.MovieId, ma.ActorId });
    }
}
