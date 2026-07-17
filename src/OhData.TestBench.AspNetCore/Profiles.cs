using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using OhData.Abstractions;

namespace OhData.TestBench.AspNetCore;

// ── Shared helpers ───────────────────────────────────────────────────────────

/// <summary>
/// The framework passes the raw <c>@odata.id</c> string from a <c>$ref</c> request body/query
/// string through to <c>addRef</c>/<c>removeRef</c> handlers unparsed (see
/// <c>OhDataEndpointFactory</c>'s <c>$ref</c> handlers) -- extracting the target key is the
/// handler's own job. This helper pulls the trailing <c>(123)</c> segment out of either a
/// relative reference (<c>"Actors(12)"</c>) or an absolute one
/// (<c>"http://host/v2/Actors(12)"</c>).
/// </summary>
internal static class ODataRefKey
{
    private static readonly Regex KeyPattern = new(@"\((\d+)\)\s*$", RegexOptions.Compiled);

    public static int ExtractInt(string odataId)
    {
        var match = KeyPattern.Match(odataId);
        if (!match.Success)
        {
            throw new FormatException($"Could not extract an integer key from '@odata.id' value '{odataId}'.");
        }
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Response shape for the <c>Rate</c> bound action. Deliberately NOT <see cref="Movie"/> itself:
/// Microsoft.OData.ModelBuilder's <c>ActionConfiguration.Returns&lt;T&gt;()</c> (which is what
/// <c>EntitySetProfile</c>'s reflection-based operation registration calls) throws
/// <c>InvalidOperationException</c> ("already declared as an entity type... use
/// ReturnsFromEntitySet") when <c>T</c> is a type already registered via <c>EntitySet&lt;T&gt;()</c>
/// -- which <see cref="Movie"/> is, for both this and every entity set that reaches it via
/// $expand. A small, single-purpose result type sidesteps that entirely.
/// </summary>
public class RatingResult
{
    public int MovieId { get; set; }
    public decimal Rating { get; set; }
    public int RatingCount { get; set; }
}

/// <summary>
/// CRUD handler logic shared between <see cref="MovieProfile"/> (v1) and
/// <see cref="MovieProfileV2"/> (v2). Kept as static factory methods rather than a common base
/// class: the two profiles differ in <c>AllowDeepInsert</c> (an <c>init</c>-only property set
/// once per concrete profile type) and in which navigation overloads they call for
/// <c>Cast</c>/<c>Studio</c>, so they're independent <see cref="EntitySetProfile{TKey,TModel}"/>
/// subclasses that both delegate here for the parts that really are identical.
/// </summary>
internal static class MovieHandlers
{
    public static Func<CancellationToken, Task<IQueryable<Movie>>> GetQueryable(AppDbContext db) =>
        (_) => Task.FromResult(db.Movies.AsQueryable());

    public static Func<int, CancellationToken, Task<Movie?>> GetById(AppDbContext db) =>
        (id, _) => Task.FromResult(db.Movies.Find(id));

    /// <summary>v1's Post: AllowDeepInsert is false there, so any nested "cast" the client sent
    /// has already been stripped (nulled) by the framework before this runs -- see
    /// EntitySetProfile.AllowDeepInsert's doc comment. Nothing special to do.</summary>
    public static Func<Movie, CancellationToken, Task<Movie?>> PostSimple(AppDbContext db) => (movie, _) =>
    {
        db.Movies.Add(movie);
        db.SaveChanges();
        return Task.FromResult<Movie?>(movie);
    };

    /// <summary>v2's Post: AllowDeepInsert is true there, so a nested "cast" array in the
    /// request body survives deserialization onto <c>movie.Cast</c> -- but only as stubs
    /// (whatever fields the client sent; typically just "id"). This framework doesn't
    /// implement <c>@odata.bind</c>, so there's no built-in way to say "link to an existing
    /// entity" -- this handler adopts its own convention instead: treat each nested cast entry
    /// as a reference to an EXISTING <see cref="Actor"/> by id (unknown ids are silently
    /// skipped rather than creating placeholder actors). Persisted atomically: the movie row,
    /// then the cast links, in the same handler invocation -- the framework opens no
    /// transaction on the handler's behalf (see AllowDeepInsert's doc comment), so a real
    /// production handler would likely wrap both SaveChanges calls in a DbContext transaction.</summary>
    public static Func<Movie, CancellationToken, Task<Movie?>> PostDeepInsert(AppDbContext db) => (movie, _) =>
    {
        List<Actor> castStubs = movie.Cast.ToList();
        movie.Cast = new List<Actor>(); // avoid EF trying to insert/duplicate-track the stubs
        db.Movies.Add(movie);
        db.SaveChanges(); // assigns movie.Id

        foreach (Actor stub in castStubs.Where(stub => db.Actors.Any(a => a.Id == stub.Id)))
        {
            db.MovieActors.Add(new MovieActor { MovieId = movie.Id, ActorId = stub.Id });
        }
        db.SaveChanges();
        return Task.FromResult<Movie?>(movie);
    };

    public static Func<int, Movie, CancellationToken, Task<Movie>> Put(AppDbContext db) => (id, movie, _) =>
    {
        var existing = db.Movies.Find(id);
        if (existing is null) return Task.FromResult<Movie>(null!);
        existing.Title = movie.Title;
        existing.Year = movie.Year;
        existing.Rating = movie.Rating;
        existing.RuntimeMinutes = movie.RuntimeMinutes;
        existing.GenreCode = movie.GenreCode;
        existing.StudioId = movie.StudioId;
        existing.ReleaseDate = movie.ReleaseDate;
        existing.UpdatedAt = DateTimeOffset.UtcNow; // keeps the ETag (Id, UpdatedAt) fresh
        db.SaveChanges();
        return Task.FromResult(existing);
    };

    public static Func<int, Delta<Movie>, CancellationToken, Task<Movie?>> Patch(AppDbContext db) => (id, delta, _) =>
    {
        var existing = db.Movies.Find(id);
        if (existing is null) return Task.FromResult<Movie?>(null);
        delta.Patch(existing);
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        db.SaveChanges();
        return Task.FromResult<Movie?>(existing);
    };

    public static Func<int, CancellationToken, Task<bool>> Delete(AppDbContext db) => (id, _) =>
    {
        var existing = db.Movies.Find(id);
        if (existing is null) return Task.FromResult(false);
        db.Movies.Remove(existing); // EF cascades the MovieActor join rows for this movie
        db.SaveChanges();
        return Task.FromResult(true);
    };

    /// <summary>GET /Movies/TopRated?count=5 -- backs the <c>TopRated</c> bound function on
    /// both profiles.</summary>
    public static Task<IEnumerable<Movie>> TopRated(AppDbContext db, int count) =>
        Task.FromResult(db.Movies
            .OrderByDescending(m => m.Rating)
            .ThenBy(m => m.Title) // EF Core InMemory can't translate an explicit IComparer<string>
            .Take(Math.Max(count, 0))
            .AsEnumerable());

    /// <summary>POST /Movies({key})/Rate { "rating": 8.5 } -- backs the <c>Rate</c> bound
    /// action on both profiles. Folds the new rating into a running average (RatingCount
    /// increments each call) instead of overwriting <see cref="Movie.Rating"/> outright, so
    /// repeated calls are observable. Returns a <see cref="RatingResult"/> rather than the
    /// updated <see cref="Movie"/> -- see that type's doc comment for why.</summary>
    public static Task<RatingResult?> Rate(AppDbContext db, int key, decimal rating)
    {
        var movie = db.Movies.Find(key);
        if (movie is null) return Task.FromResult<RatingResult?>(null);
        decimal total = movie.Rating * movie.RatingCount + rating;
        movie.RatingCount += 1;
        movie.Rating = Math.Round(total / movie.RatingCount, 2);
        movie.UpdatedAt = DateTimeOffset.UtcNow;
        db.SaveChanges();
        return Task.FromResult<RatingResult?>(
            new RatingResult { MovieId = movie.Id, Rating = movie.Rating, RatingCount = movie.RatingCount });
    }
}

// ── Movies (v1) ───────────────────────────────────────────────────────────────

/// <summary>
/// v1's movie profile: full CRUD over EF Core InMemory via <c>GetQueryable</c> (SQL-pushdown
/// $filter/$orderby/$skip/$top -- see CLAUDE.md "Two paths for GET collection"), ETags, and the
/// TopRated/Rate bound operations. Deliberately simpler than v2 -- see the AllowDeepInsert and
/// Cast/Studio comments below for what v1 leaves out and why.
/// </summary>
public class MovieProfile : EntitySetProfile<int, Movie>
{
    private readonly AppDbContext _db;

    public MovieProfile(AppDbContext db) : base(x => x.Id)
    {
        _db = db;
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        CountEnabled = true;
        MaxTop = 50;

        // v1 leaves AllowDeepInsert at its default (false, see EntitySetDefaults): any nested
        // "cast" array in a v1 POST body is stripped (nulled) before Post runs, so a v1 POST
        // never silently creates cast links from a partially-understood request. Compare with
        // MovieProfileV2, which opts in (AllowDeepInsert = true) and documents what that adds.
        //
        // ExpandEnabled is also left off (default false): Cast/Studio are declared below for
        // EDM/$metadata completeness only, via the no-handler overloads -- "handler presence
        // drives route registration" (CLAUDE.md), so v1 registers no GET .../Cast, no $ref, and
        // has nothing for $expand to call into. v2 adds real handlers for both.
        HasMany(x => x.Cast);
        HasRequired(x => x.Studio);

        // ── ETag teaching comment (also see MovieProfileV2) ──────────────────
        // UpdatedAt is bumped on every write (MovieHandlers.Put/Patch/Rate). UseETag hashes it
        // together with Id into the ETag response header on GET/POST/PUT/PATCH (OData §8.2.6),
        // and the framework checks If-Match on PUT/PATCH/DELETE: read a movie, note its ETag,
        // then PUT/PATCH it with that value as If-Match -- if something else updated the movie
        // in between (another PATCH, or a Rate() call), the ETag has moved on and the request
        // gets 412 Precondition Failed instead of silently clobbering the other write.
        UseETag(x => x.Id, x => x.UpdatedAt);

        GetQueryable = MovieHandlers.GetQueryable(db);
        GetById = MovieHandlers.GetById(db);
        Post = MovieHandlers.PostSimple(db);
        Put = MovieHandlers.Put(db);
        Patch = MovieHandlers.Patch(db);
        Delete = MovieHandlers.Delete(db);

        BindFunction(TopRated);
        BindEntityAction(Rate);
    }

    // Collection-bound function (OData §11.5.3): GET /Movies/TopRated?count=5
    private Task<IEnumerable<Movie>> TopRated(int count = 10) => MovieHandlers.TopRated(_db, count);

    // Entity-bound action (OData §11.5.4): POST /Movies({key})/Rate { "rating": 8.5 }
    private Task<RatingResult?> Rate(int key, decimal rating) => MovieHandlers.Rate(_db, key, rating);
}

// ── Movies (v2) ───────────────────────────────────────────────────────────────

/// <summary>
/// v2's movie profile: everything v1 has, plus deep insert and fully-handled Cast/Studio
/// navigations (batch $expand, $ref link management -- see the comments on each below for why
/// they're split the way they are).
/// </summary>
public class MovieProfileV2 : EntitySetProfile<int, Movie>
{
    private readonly AppDbContext _db;

    public MovieProfileV2(AppDbContext db) : base(x => x.Id)
    {
        _db = db;
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;
        MaxTop = 50;

        // Deep insert (OData §11.4.2.2): POST /v2/Movies with a nested "cast" array is passed
        // through to Post as-is instead of being stripped -- see MovieHandlers.PostDeepInsert
        // for how the handler treats each nested stub as a reference to an existing Actor by id.
        AllowDeepInsert = true;

        UseETag(x => x.Id, x => x.UpdatedAt);

        // Cast: HasMany's batch overload (batchGetAll) and its getAll/addRef/removeRef overload
        // are mutually exclusive in this framework version -- there is no single HasMany call
        // that registers both a SQL-batched $expand AND $ref link management on the same
        // navigation (see the HasMany overloads on EntitySetProfile). This profile picks $ref
        // link management for Cast, since "POST/DELETE .../Cast/$ref" is the more concrete,
        // directly-testable behavior for a demo. $expand=Cast still works correctly here via
        // the automatic per-entity fallback every HasMany handler gets -- a NavigationRoute
        // with no BatchHandler "falls back byte-identically to the per-entity path" (CLAUDE.md)
        // -- it just issues one query per movie in the page instead of one for the whole page.
        // The true SQL-batched $expand pattern is showcased instead on Movie.Studio (below) and
        // on the reverse Studio.Movies navigation (see StudioProfile).
        HasMany(
            navigation: x => x.Cast,
            getAll: (movieId, ct) => Task.FromResult<IEnumerable<Actor>>(
                db.MovieActors
                    .Where(ma => ma.MovieId == movieId)
                    .Join(db.Actors, ma => ma.ActorId, a => a.Id, (ma, a) => a)
                    .ToList()),
            post: null,
            refTargetEntitySet: "Actors",
            addRef: (movieId, relatedId, ct) =>
            {
                int actorId = ODataRefKey.ExtractInt(relatedId);
                if (db.Actors.Any(a => a.Id == actorId) &&
                    !db.MovieActors.Any(ma => ma.MovieId == movieId && ma.ActorId == actorId))
                {
                    db.MovieActors.Add(new MovieActor { MovieId = movieId, ActorId = actorId });
                    db.SaveChanges();
                }
                return Task.CompletedTask;
            },
            removeRef: (movieId, relatedId, ct) =>
            {
                int actorId = ODataRefKey.ExtractInt(relatedId);
                var link = db.MovieActors.FirstOrDefault(ma => ma.MovieId == movieId && ma.ActorId == actorId);
                if (link is not null)
                {
                    db.MovieActors.Remove(link);
                    db.SaveChanges();
                }
                return Task.CompletedTask;
            });

        // Studio: batch-loaded single-valued navigation (see CLAUDE.md "Batch-aware $expand").
        // $expand=Studio on a page of movies loads every distinct studio on that page with ONE
        // query instead of one query per movie. refTargetEntitySet also gives
        // GET /Movies({key})/Studio/$ref a populated @odata.id (read-only -- HasRequired's
        // batch overload has no setRef/removeRef; Studio is a required navigation here, and
        // reassigning it is exercised through PATCH { "studioId": ... } instead).
        HasRequired(
            navigation: x => x.Studio,
            batchGet: (movieIds, ct) =>
            {
                var idSet = movieIds.ToHashSet();
                Dictionary<int, Studio> map = db.Movies
                    .Where(m => idSet.Contains(m.Id))
                    .Join(db.Studios, m => m.StudioId, s => s.Id, (m, s) => new { m.Id, Studio = s })
                    .ToDictionary(x => x.Id, x => x.Studio);
                return Task.FromResult<IReadOnlyDictionary<int, Studio>>(map);
            },
            refTargetEntitySet: "Studios");

        GetQueryable = MovieHandlers.GetQueryable(db);
        GetById = MovieHandlers.GetById(db);
        Post = MovieHandlers.PostDeepInsert(db);
        Put = MovieHandlers.Put(db);
        Patch = MovieHandlers.Patch(db);
        Delete = MovieHandlers.Delete(db);

        BindFunction(TopRated);
        BindEntityAction(Rate);
    }

    private Task<IEnumerable<Movie>> TopRated(int count = 10) => MovieHandlers.TopRated(_db, count);
    private Task<RatingResult?> Rate(int key, decimal rating) => MovieHandlers.Rate(_db, key, rating);
}

// ── Genres ────────────────────────────────────────────────────────────────────

/// <summary>
/// A small, static genre lookup -- deliberately the <c>GetAll</c> (<see cref="IEnumerable{T}"/>)
/// showcase, in contrast to <see cref="MovieProfile"/>'s <c>GetQueryable</c>.
/// <para>
/// <b>GetAll vs GetQueryable:</b> <c>GetAll</c> hands the framework a plain in-memory
/// <see cref="IEnumerable{T}"/> and it is returned as-is -- no <c>$filter</c>/<c>$orderby</c>/
/// <c>$skip</c>/<c>$top</c> pushdown, nothing to translate to SQL, nothing to misconfigure. It's
/// the right choice for small, mostly-static reference data like this 11-row genre list, where
/// "return everything" is already fast and correct. <c>GetQueryable</c> (see
/// <see cref="MovieProfile"/>) is the right choice once the backing store can push query options
/// down to SQL -- use it for anything EF Core-backed or otherwise large enough that "load
/// everything, then filter in memory" would be wasteful. Genres here never grows past a dozen
/// rows, so that tradeoff isn't worth making.
/// </para>
/// </summary>
public class GenreProfile : EntitySetProfile<string, Genre>
{
    public GenreProfile() : base(x => x.Code)
    {
        EntitySetName = "Genres";

        GetAll = (_) => Task.FromResult<IEnumerable<Genre>>(DbSeeder.Genres);
        GetById = (code, _) => Task.FromResult(
            DbSeeder.Genres.FirstOrDefault(g => string.Equals(g.Code, code, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>
/// v2 variant of <see cref="GenreProfile"/> -- identical configuration, separate DI registration
/// so it can coexist in the v2 OhData registration alongside v1's <see cref="GenreProfile"/>.
/// </summary>
public class GenreProfileV2 : GenreProfile { }

// ── Actors (v2 only) ─────────────────────────────────────────────────────────

/// <summary>Queryable actor catalog. v2 only -- v1 keeps its surface to Movies/Genres.</summary>
public class ActorProfile : EntitySetProfile<int, Actor>
{
    public ActorProfile(AppDbContext db) : base(x => x.Id)
    {
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        CountEnabled = true;
        MaxTop = 50;

        GetQueryable = (_) => Task.FromResult(db.Actors.AsQueryable());
        GetById = (id, _) => Task.FromResult(db.Actors.Find(id));

        Post = (actor, _) =>
        {
            db.Actors.Add(actor);
            db.SaveChanges();
            return Task.FromResult<Actor?>(actor);
        };

        Put = (id, actor, _) =>
        {
            var existing = db.Actors.Find(id);
            if (existing is null) return Task.FromResult<Actor>(null!);
            existing.Name = actor.Name;
            existing.BirthYear = actor.BirthYear;
            db.SaveChanges();
            return Task.FromResult(existing);
        };

        Patch = (id, delta, _) =>
        {
            var existing = db.Actors.Find(id);
            if (existing is null) return Task.FromResult<Actor?>(null);
            delta.Patch(existing);
            db.SaveChanges();
            return Task.FromResult<Actor?>(existing);
        };

        Delete = (id, _) =>
        {
            var existing = db.Actors.Find(id);
            if (existing is null) return Task.FromResult(false);
            db.Actors.Remove(existing);
            db.SaveChanges();
            return Task.FromResult(true);
        };
    }
}

// ── Studios (v2 only) ────────────────────────────────────────────────────────

/// <summary>Queryable studio catalog. v2 only, with a batch-loaded reverse Movies navigation.</summary>
public class StudioProfile : EntitySetProfile<int, Studio>
{
    public StudioProfile(AppDbContext db) : base(x => x.Id)
    {
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;
        MaxTop = 50;

        // Batch-loaded collection navigation: $expand=Movies on GET /Studios loads every
        // expanded studio's movies with ONE query for the whole page (CLAUDE.md "Batch-aware
        // $expand") instead of one query per studio -- the reverse side of the same pattern
        // MovieProfileV2 uses for Movie.Studio.
        HasMany(x => x.Movies, batchGetAll: (studioIds, ct) =>
        {
            var idSet = studioIds.ToHashSet();
            ILookup<int, Movie> lookup = db.Movies
                .Where(m => idSet.Contains(m.StudioId))
                .AsEnumerable()
                .ToLookup(m => m.StudioId);
            return Task.FromResult(lookup);
        });

        GetQueryable = (_) => Task.FromResult(db.Studios.AsQueryable());
        GetById = (id, _) => Task.FromResult(db.Studios.Find(id));

        Post = (studio, _) =>
        {
            db.Studios.Add(studio);
            db.SaveChanges();
            return Task.FromResult<Studio?>(studio);
        };

        Put = (id, studio, _) =>
        {
            var existing = db.Studios.Find(id);
            if (existing is null) return Task.FromResult<Studio>(null!);
            existing.Name = studio.Name;
            existing.Founded = studio.Founded;
            db.SaveChanges();
            return Task.FromResult(existing);
        };

        Patch = (id, delta, _) =>
        {
            var existing = db.Studios.Find(id);
            if (existing is null) return Task.FromResult<Studio?>(null);
            delta.Patch(existing);
            db.SaveChanges();
            return Task.FromResult<Studio?>(existing);
        };

        Delete = (id, _) =>
        {
            var existing = db.Studios.Find(id);
            if (existing is null) return Task.FromResult(false);
            db.Studios.Remove(existing);
            db.SaveChanges();
            return Task.FromResult(true);
        };
    }
}
