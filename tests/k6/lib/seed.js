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

// -- Awards: the polymorphic (TPH) set (#617) ---------------------------------
// Three rows, three shapes: an AcademyAward, a FestivalAward and a bare Award. The MIX is the
// point -- a projection over the declared type serves the base row correctly and drops the derived
// rows' own properties, so a single-shape fixture would prove nothing (#529).
export const AWARD_COUNT = 3;
export const ACADEMY_AWARD_ID = 1;      // Best Picture, Ceremony + IsWinner
export const ACADEMY_AWARD_CEREMONY = '67th Academy Awards';
export const ACADEMY_AWARD_NOMINATIONS = 3;
export const FESTIVAL_AWARD_ID = 2;     // Palme d'Or, Festival + Jury
export const FESTIVAL_AWARD_FESTIVAL = 'Cannes';
export const PLAIN_AWARD_ID = 3;        // no derived members at all
// Exactly one nomination on each of awards 1 and 2 contains "Pulp"; award 3 has none.
export const AWARD_NOMINATION_NEEDLE = 'Pulp';

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
