using System;
using System.Linq;
using OhData.TestBench.AspNetCore;

namespace OhData.TestBench.AspNetCore;

/// <summary>
/// Deterministic, hand-curated seed data for the movie catalog demo.
/// <para>
/// Titles, years, genres, studios, and principal cast are real, well-known films chosen for
/// recognizability -- so a visitor's first <c>$filter</c>/<c>$orderby</c> experiment returns
/// movies they actually know. <see cref="Movie.Rating"/> is NOT sourced from IMDb, Rotten
/// Tomatoes, or any other rating aggregator -- every value below is invented for this demo and
/// is purely illustrative. Studio assignments reflect the primary production/distribution studio
/// as commonly credited; a few conglomerate-era mergers and international co-productions are
/// simplified for clarity (this is a demo, not a filmography of record).
/// </para>
/// <para>
/// No external API is called to build this data (no TMDB/OMDb, no dataset dump) -- it is a
/// static, deterministic array literal, safe to seed on every cold start.
/// </para>
/// </summary>
public static class DbSeeder
{
    public static void Seed(AppDbContext db)
    {
        if (db.Movies.Any()) return;

        db.Studios.AddRange(Studios);
        db.Actors.AddRange(Actors);
        db.SaveChanges();

        db.Movies.AddRange(Movies.Select(m => m.ToMovie()));
        db.SaveChanges();

        foreach (var m in Movies)
        {
            foreach (int actorId in m.CastIds)
            {
                db.MovieActors.Add(new MovieActor { MovieId = m.Id, ActorId = actorId });
            }
        }
        db.SaveChanges();
    }

    // ── Genres — the GetAll (IEnumerable) showcase; see GenreProfile ─────────────
    public static readonly Genre[] Genres =
    {
        new() { Code = "ACTION",    Name = "Action" },
        new() { Code = "ADVENTURE", Name = "Adventure" },
        new() { Code = "ANIMATION", Name = "Animation" },
        new() { Code = "COMEDY",    Name = "Comedy" },
        new() { Code = "CRIME",     Name = "Crime" },
        new() { Code = "DRAMA",     Name = "Drama" },
        new() { Code = "FANTASY",   Name = "Fantasy" },
        new() { Code = "HORROR",    Name = "Horror" },
        new() { Code = "ROMANCE",   Name = "Romance" },
        new() { Code = "SCIFI",     Name = "Science Fiction" },
        new() { Code = "THRILLER",  Name = "Thriller" },
    };

    // ── Studios ────────────────────────────────────────────────────────────────
    public static readonly Studio[] Studios =
    {
        new() { Id = 1, Name = "Warner Bros. Pictures", Founded = 1923 },
        new() { Id = 2, Name = "Universal Pictures", Founded = 1912 },
        new() { Id = 3, Name = "Paramount Pictures", Founded = 1912 },
        new() { Id = 4, Name = "Walt Disney Pictures", Founded = 1923 },
        new() { Id = 5, Name = "Columbia Pictures", Founded = 1924 },
        new() { Id = 6, Name = "20th Century Studios", Founded = 1935 },
        new() { Id = 7, Name = "Metro-Goldwyn-Mayer", Founded = 1924 },
        new() { Id = 8, Name = "New Line Cinema", Founded = 1967 },
    };

    // ── Actors — real principal cast members, deliberately reused across their
    // real filmographies below so the catalog stays richly cross-linked ────────
    public static readonly Actor[] Actors =
    {
        new() { Id = 1, Name = "Al Pacino", BirthYear = 1940 },
        new() { Id = 2, Name = "Robert Duvall", BirthYear = 1931 },
        new() { Id = 3, Name = "Jack Nicholson", BirthYear = 1937 },
        new() { Id = 4, Name = "Roy Scheider", BirthYear = 1932 },
        new() { Id = 5, Name = "Sylvester Stallone", BirthYear = 1946 },
        new() { Id = 6, Name = "Mark Hamill", BirthYear = 1951 },
        new() { Id = 7, Name = "Harrison Ford", BirthYear = 1942 },
        new() { Id = 8, Name = "Carrie Fisher", BirthYear = 1956 },
        new() { Id = 9, Name = "Sigourney Weaver", BirthYear = 1949 },
        new() { Id = 10, Name = "Martin Sheen", BirthYear = 1940 },
        new() { Id = 11, Name = "Bruce Willis", BirthYear = 1955 },
        new() { Id = 12, Name = "Alan Rickman", BirthYear = 1946 },
        new() { Id = 13, Name = "Michael Keaton", BirthYear = 1951 },
        new() { Id = 14, Name = "Robert De Niro", BirthYear = 1943 },
        new() { Id = 15, Name = "Arnold Schwarzenegger", BirthYear = 1947 },
        new() { Id = 16, Name = "Linda Hamilton", BirthYear = 1956 },
        new() { Id = 17, Name = "Anthony Hopkins", BirthYear = 1937 },
        new() { Id = 18, Name = "Jeff Goldblum", BirthYear = 1952 },
        new() { Id = 19, Name = "Morgan Freeman", BirthYear = 1937 },
        new() { Id = 20, Name = "Samuel L. Jackson", BirthYear = 1948 },
        new() { Id = 21, Name = "Tom Hanks", BirthYear = 1956 },
        new() { Id = 22, Name = "Brad Pitt", BirthYear = 1963 },
        new() { Id = 23, Name = "Leonardo DiCaprio", BirthYear = 1974 },
        new() { Id = 24, Name = "Kate Winslet", BirthYear = 1975 },
        new() { Id = 25, Name = "Keanu Reeves", BirthYear = 1964 },
        new() { Id = 26, Name = "Carrie-Anne Moss", BirthYear = 1967 },
        new() { Id = 27, Name = "Edward Norton", BirthYear = 1969 },
        new() { Id = 28, Name = "Joaquin Phoenix", BirthYear = 1974 },
        new() { Id = 29, Name = "Elijah Wood", BirthYear = 1981 },
        new() { Id = 30, Name = "Ian McKellen", BirthYear = 1939 },
        new() { Id = 31, Name = "Viggo Mortensen", BirthYear = 1958 },
        new() { Id = 32, Name = "Christian Bale", BirthYear = 1974 },
        new() { Id = 33, Name = "Matt Damon", BirthYear = 1970 },
        new() { Id = 34, Name = "Heath Ledger", BirthYear = 1979 },
        new() { Id = 35, Name = "Sam Worthington", BirthYear = 1976 },
        new() { Id = 36, Name = "Zoe Saldana", BirthYear = 1978 },
        new() { Id = 37, Name = "Jamie Foxx", BirthYear = 1967 },
        new() { Id = 38, Name = "Robert Downey Jr.", BirthYear = 1965 },
        new() { Id = 39, Name = "Chris Evans", BirthYear = 1981 },
        new() { Id = 40, Name = "Chris Hemsworth", BirthYear = 1983 },
        new() { Id = 41, Name = "Josh Brolin", BirthYear = 1968 },
        new() { Id = 42, Name = "Tom Hardy", BirthYear = 1977 },
        new() { Id = 43, Name = "Chadwick Boseman", BirthYear = 1976 },
        new() { Id = 44, Name = "Timothée Chalamet", BirthYear = 1995 },
        new() { Id = 45, Name = "Zendaya", BirthYear = 1996 },
        new() { Id = 46, Name = "Rebecca Ferguson", BirthYear = 1983 },
        new() { Id = 47, Name = "Margot Robbie", BirthYear = 1990 },
        new() { Id = 48, Name = "Tom Cruise", BirthYear = 1962 },
        new() { Id = 49, Name = "Gal Gadot", BirthYear = 1985 },
        new() { Id = 50, Name = "Cillian Murphy", BirthYear = 1976 },
        new() { Id = 51, Name = "Amy Poehler", BirthYear = 1971 },
        new() { Id = 52, Name = "Maya Hawke", BirthYear = 1998 },
    };

    /// <summary>Plain seed record; converted to a <see cref="Movie"/> without the cast collection
    /// (cast is linked separately via <see cref="MovieActor"/> rows after both sides exist).</summary>
    private sealed record SeedMovie(
        int Id, string Title, int Year, string GenreCode, int StudioId, int RuntimeMinutes,
        DateOnly ReleaseDate, decimal Rating, int[] CastIds)
    {
        public Movie ToMovie() => new()
        {
            Id = Id,
            Title = Title,
            Year = Year,
            GenreCode = GenreCode,
            StudioId = StudioId,
            RuntimeMinutes = RuntimeMinutes,
            ReleaseDate = ReleaseDate,
            Rating = Rating,
            RatingCount = 1,
            UpdatedAt = new DateTimeOffset(ReleaseDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        };
    }

    // 77 real, widely-recognizable films spanning 1972-2024. Ratings are synthetic (see class
    // doc comment above). Cast lists are trimmed to 1-4 real principal actors drawn from the
    // 52-name roster above, chosen so prolific actors recur across their real filmography.
    private static readonly SeedMovie[] Movies =
    {
        new(1, "The Godfather", 1972, "CRIME", 7, 175, new(1972, 3, 24), 9.3m, new[] { 1, 2 }),
        new(2, "One Flew Over the Cuckoo's Nest", 1975, "DRAMA", 7, 133, new(1975, 11, 19), 8.9m, new[] { 3 }),
        new(3, "Jaws", 1975, "THRILLER", 2, 124, new(1975, 6, 20), 8.4m, new[] { 4 }),
        new(4, "Rocky", 1976, "DRAMA", 7, 120, new(1976, 11, 21), 8.0m, new[] { 5 }),
        new(5, "Star Wars", 1977, "SCIFI", 6, 121, new(1977, 5, 25), 9.1m, new[] { 6, 7, 8 }),
        new(6, "Alien", 1979, "HORROR", 6, 117, new(1979, 5, 25), 8.6m, new[] { 9 }),
        new(7, "Apocalypse Now", 1979, "DRAMA", 7, 147, new(1979, 8, 15), 8.7m, new[] { 10, 2 }),

        new(8, "The Empire Strikes Back", 1980, "SCIFI", 6, 124, new(1980, 5, 21), 9.2m, new[] { 6, 7, 8 }),
        new(9, "Raiders of the Lost Ark", 1981, "ADVENTURE", 3, 115, new(1981, 6, 12), 8.8m, new[] { 7 }),
        new(10, "Blade Runner", 1982, "SCIFI", 1, 117, new(1982, 6, 25), 8.3m, new[] { 7 }),
        new(11, "Scarface", 1983, "CRIME", 2, 170, new(1983, 12, 9), 8.4m, new[] { 1 }),
        new(12, "The Terminator", 1984, "SCIFI", 7, 107, new(1984, 10, 26), 8.2m, new[] { 15, 16 }),
        new(13, "Ghostbusters", 1984, "COMEDY", 5, 105, new(1984, 6, 8), 8.1m, new[] { 9 }),
        new(14, "Top Gun", 1986, "ACTION", 3, 110, new(1986, 5, 16), 7.6m, new[] { 48 }),
        new(15, "Aliens", 1986, "SCIFI", 6, 137, new(1986, 7, 18), 8.5m, new[] { 9 }),
        new(16, "Predator", 1987, "ACTION", 6, 107, new(1987, 6, 12), 7.8m, new[] { 15 }),
        new(17, "Big", 1988, "FANTASY", 6, 104, new(1988, 6, 3), 7.5m, new[] { 21 }),
        new(18, "Die Hard", 1988, "ACTION", 6, 132, new(1988, 7, 15), 8.6m, new[] { 11, 12 }),
        new(19, "Batman", 1989, "ACTION", 1, 126, new(1989, 6, 23), 7.7m, new[] { 13, 3 }),

        new(20, "Goodfellas", 1990, "CRIME", 1, 146, new(1990, 9, 19), 8.9m, new[] { 14 }),
        new(21, "Terminator 2: Judgment Day", 1991, "SCIFI", 5, 137, new(1991, 7, 3), 8.8m, new[] { 15, 16 }),
        new(22, "The Silence of the Lambs", 1991, "THRILLER", 7, 118, new(1991, 2, 14), 8.9m, new[] { 17 }),
        new(23, "Jurassic Park", 1993, "SCIFI", 2, 127, new(1993, 6, 11), 8.7m, new[] { 18 }),
        new(24, "The Shawshank Redemption", 1994, "DRAMA", 5, 142, new(1994, 9, 23), 9.4m, new[] { 19 }),
        new(25, "Pulp Fiction", 1994, "CRIME", 4, 154, new(1994, 10, 14), 8.9m, new[] { 20 }),
        new(26, "Forrest Gump", 1994, "DRAMA", 3, 142, new(1994, 7, 6), 8.6m, new[] { 21 }),
        new(27, "Se7en", 1995, "THRILLER", 8, 127, new(1995, 9, 22), 8.5m, new[] { 22, 19 }),
        new(28, "Toy Story", 1995, "ANIMATION", 4, 81, new(1995, 11, 22), 8.2m, new[] { 21 }),
        new(29, "Independence Day", 1996, "SCIFI", 6, 145, new(1996, 7, 3), 7.2m, new[] { 18 }),
        new(30, "Titanic", 1997, "ROMANCE", 3, 195, new(1997, 12, 19), 8.5m, new[] { 23, 24 }),
        new(31, "The Matrix", 1999, "SCIFI", 1, 136, new(1999, 3, 31), 8.9m, new[] { 25, 26 }),
        new(32, "Fight Club", 1999, "DRAMA", 6, 139, new(1999, 10, 15), 8.7m, new[] { 22, 27 }),

        new(33, "Gladiator", 2000, "ACTION", 2, 155, new(2000, 5, 5), 8.4m, new[] { 28 }),
        new(34, "Memento", 2000, "THRILLER", 8, 113, new(2000, 10, 11), 8.1m, new[] { 26 }),
        new(35, "American Psycho", 2000, "THRILLER", 8, 102, new(2000, 4, 14), 7.6m, new[] { 32 }),
        new(36, "The Lord of the Rings: The Fellowship of the Ring", 2001, "FANTASY", 8, 178, new(2001, 12, 19), 9.0m, new[] { 29, 30, 31 }),
        new(37, "Catch Me If You Can", 2002, "CRIME", 4, 141, new(2002, 12, 25), 7.9m, new[] { 23 }),
        new(38, "The Lord of the Rings: The Return of the King", 2003, "FANTASY", 8, 201, new(2003, 12, 17), 9.1m, new[] { 29, 30, 31 }),
        new(39, "The Last Samurai", 2003, "ACTION", 1, 154, new(2003, 12, 5), 7.4m, new[] { 48 }),
        new(40, "Eternal Sunshine of the Spotless Mind", 2004, "ROMANCE", 2, 108, new(2004, 3, 19), 8.3m, new[] { 24 }),
        new(41, "Batman Begins", 2005, "ACTION", 1, 140, new(2005, 6, 15), 8.1m, new[] { 32 }),
        new(42, "The Departed", 2006, "CRIME", 1, 151, new(2006, 10, 6), 8.5m, new[] { 23, 33, 3 }),
        new(43, "No Country for Old Men", 2007, "THRILLER", 3, 122, new(2007, 11, 9), 8.1m, new[] { 41 }),
        new(44, "The Dark Knight", 2008, "ACTION", 1, 152, new(2008, 7, 18), 9.0m, new[] { 32, 34 }),
        new(45, "Iron Man", 2008, "ACTION", 4, 126, new(2008, 5, 2), 7.9m, new[] { 38 }),
        new(46, "Sherlock Holmes", 2009, "ACTION", 1, 128, new(2009, 12, 25), 7.5m, new[] { 38 }),
        new(47, "Avatar", 2009, "SCIFI", 6, 162, new(2009, 12, 18), 7.8m, new[] { 35, 36 }),

        new(48, "Inception", 2010, "SCIFI", 1, 148, new(2010, 7, 16), 8.8m, new[] { 23 }),
        new(49, "The Fighter", 2010, "DRAMA", 3, 116, new(2010, 12, 10), 7.3m, new[] { 32 }),
        new(50, "Django Unchained", 2012, "DRAMA", 5, 165, new(2012, 12, 25), 8.3m, new[] { 23 }),
        new(51, "The Avengers", 2012, "ACTION", 4, 143, new(2012, 5, 4), 8.0m, new[] { 38, 39 }),
        new(52, "World War Z", 2013, "THRILLER", 3, 116, new(2013, 6, 21), 6.9m, new[] { 22 }),
        new(53, "The Wolf of Wall Street", 2013, "CRIME", 3, 180, new(2013, 12, 25), 8.2m, new[] { 23, 47 }),
        new(54, "Fury", 2014, "DRAMA", 5, 134, new(2014, 10, 17), 7.5m, new[] { 22 }),
        new(55, "Mad Max: Fury Road", 2015, "ACTION", 1, 120, new(2015, 5, 15), 8.1m, new[] { 42 }),
        new(56, "The Revenant", 2015, "DRAMA", 6, 156, new(2015, 12, 25), 8.0m, new[] { 23, 42 }),
        new(57, "Captain America: Civil War", 2016, "ACTION", 4, 147, new(2016, 5, 6), 7.8m, new[] { 39, 38 }),
        new(58, "Dunkirk", 2017, "DRAMA", 1, 106, new(2017, 7, 21), 7.8m, new[] { 42 }),
        new(59, "Justice League", 2017, "ACTION", 1, 120, new(2017, 11, 17), 6.4m, new[] { 49 }),
        new(60, "Wonder Woman", 2017, "ACTION", 1, 141, new(2017, 6, 2), 7.5m, new[] { 49 }),
        new(61, "Black Panther", 2018, "ACTION", 4, 134, new(2018, 2, 16), 7.7m, new[] { 43 }),
        new(62, "Avengers: Infinity War", 2018, "ACTION", 4, 149, new(2018, 4, 27), 8.5m, new[] { 38, 40, 41, 43 }),
        new(63, "Mission: Impossible - Fallout", 2018, "ACTION", 3, 147, new(2018, 7, 27), 7.9m, new[] { 48 }),
        new(64, "Joker", 2019, "DRAMA", 1, 122, new(2019, 10, 4), 8.0m, new[] { 28, 14 }),
        new(65, "Avengers: Endgame", 2019, "ACTION", 4, 181, new(2019, 4, 26), 8.5m, new[] { 38, 39, 40 }),
        new(66, "Ford v Ferrari", 2019, "DRAMA", 6, 152, new(2019, 11, 15), 7.9m, new[] { 32, 33 }),

        new(67, "Soul", 2020, "ANIMATION", 4, 100, new(2020, 12, 25), 7.9m, new[] { 37 }),
        new(68, "Dune", 2021, "SCIFI", 1, 155, new(2021, 10, 22), 8.0m, new[] { 44, 46 }),
        new(69, "Spider-Man: No Way Home", 2021, "ACTION", 5, 148, new(2021, 12, 17), 8.1m, new[] { 45 }),
        new(70, "Top Gun: Maverick", 2022, "ACTION", 3, 130, new(2022, 5, 27), 8.3m, new[] { 48 }),
        new(71, "Bullet Train", 2022, "COMEDY", 5, 127, new(2022, 8, 5), 6.9m, new[] { 22 }),
        new(72, "Avatar: The Way of Water", 2022, "SCIFI", 6, 192, new(2022, 12, 16), 7.5m, new[] { 35, 36 }),
        new(73, "Oppenheimer", 2023, "DRAMA", 2, 180, new(2023, 7, 21), 8.7m, new[] { 50, 33 }),
        new(74, "Barbie", 2023, "COMEDY", 1, 114, new(2023, 7, 21), 7.4m, new[] { 47 }),
        new(75, "Killers of the Flower Moon", 2023, "CRIME", 3, 206, new(2023, 10, 20), 7.8m, new[] { 23, 14 }),
        new(76, "Dune: Part Two", 2024, "SCIFI", 1, 166, new(2024, 3, 1), 8.4m, new[] { 44, 45, 46 }),
        new(77, "Inside Out 2", 2024, "ANIMATION", 4, 96, new(2024, 6, 14), 7.6m, new[] { 51, 52 }),
    };
}
