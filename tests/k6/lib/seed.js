// Seeded TestBench facts, in one place.
//
// smoke.js and conformance.js both need these. They used to be inline constants in smoke.js;
// a second script copying them is a second thing to update when SeedData.cs changes, and the
// copy that is not updated fails for a reason that looks like a server bug. Source of truth:
// src/OhData.TestBench.AspNetCore/SeedData.cs and src/OhData.TestBench.AspNetCore/Profiles.cs.

export const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';

// ── Movies ───────────────────────────────────────────────────────────────────
// Ids 1..77, seeded by DbSeeder.Movies. Tests must not assume the count is still exactly 77
// at request time -- smoke.js creates and deletes movies -- so this is the floor, not the
// count. Anything that needs an exact number reads it from the server (see COUNT baselines
// in conformance.js).
export const SEEDED_MOVIE_COUNT = 77;

// The Godfather (1972, rating 9.3, CRIME, studio 7 = Metro-Goldwyn-Mayer, cast 1 + 2).
export const SEEDED_MOVIE_ID = 1;
export const SEEDED_MOVIE_TITLE = 'The Godfather';
export const SEEDED_MOVIE_YEAR = 1972;
export const SEEDED_MOVIE_STUDIO_ID = 7;
export const SEEDED_MOVIE_STUDIO_NAME = 'Metro-Goldwyn-Mayer';
export const SEEDED_MOVIE_CAST_COUNT = 2; // Al Pacino (1), Robert Duvall (2)

// Exactly three seeded movies have Year eq 1994: Shawshank, Pulp Fiction, Forrest Gump.
export const YEAR_1994 = 1994;
export const YEAR_1994_COUNT = 3;

// A key no seeded row uses, for 404 paths.
export const MISSING_ID = 99999;

// ── Actors / Studios (v2 only) ───────────────────────────────────────────────
export const SEEDED_ACTOR_COUNT = 52;
// Not in The Godfather's seeded cast, so $ref add/remove is observable against it.
export const UNLINKED_ACTOR_ID = 30; // Ian McKellen
export const SEEDED_STUDIO_COUNT = 8;
export const SEEDED_STUDIO_ID = 1; // Warner Bros. Pictures

// ── Genres ───────────────────────────────────────────────────────────────────
// GenreProfile is the GetAll (IEnumerable) showcase: a static 11-row array, string key.
export const SEEDED_GENRE_COUNT = 11;
export const SEEDED_GENRE_CODE = 'DRAMA';

// ── Profile configuration that the wire depends on ───────────────────────────
// MovieProfile/MovieProfileV2 both set MaxTop = 50. With 77+ movies that means a bare
// collection GET pages, which is what gives smoke.js an @odata.nextLink the SERVER issued
// to follow.
export const MOVIE_MAX_TOP = 50;

// A minimal valid Movie body. Every property the EDM declares Nullable="false" is present,
// so this is the control against which the #355/#544 nullability cases are read.
export function newMovie(overrides) {
  return Object.assign({
    title: 'K6TestMovie',
    year: 2025,
    rating: 1.25,
    ratingCount: 1,
    runtimeMinutes: 90,
    genreCode: 'DRAMA',
    studioId: 1,
    releaseDate: '2025-01-01',
    updatedAt: '2025-01-01T00:00:00Z',
  }, overrides || {});
}
