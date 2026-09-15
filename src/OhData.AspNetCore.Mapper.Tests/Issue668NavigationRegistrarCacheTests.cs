using System;
using System.Linq;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Mapper.Tests;

/// <summary>
/// The per-navigation registrar is memoised, so the reflection that closes it over the element
/// model type runs once per element type per process rather than once per request.
/// </summary>
/// <remarks>
/// Safe to memoise where the map itself is not: a registrar is a pure function of the element model
/// type, it is held on the closed profile type's own statics, and the profile reaches it as an
/// argument rather than being captured. These tests pin that the key really is the element model
/// type and that nothing of the first construction survives into the next.
/// </remarks>
public sealed class Issue668NavigationRegistrarCacheTests
{
    private static readonly Func<IQueryable<Product>> Empty = () => Array.Empty<Product>().AsQueryable();

    /// <summary>Two collection navigations of different element types, plus a reference.</summary>
    private sealed class AllNavigationsProfile : MappedEntitySetProfile<int, ProductDto, Product>
    {
        public AllNavigationsProfile() : base(d => d.Id)
        {
            EntitySetName = "All668";
            UseMap(Empty, m => m
                .Root(Maps.Declare)
                .Nested<Category, CategoryDto>(Maps.DeclareCategory)
                .Nested<Tag, TagDto>(Maps.DeclareTag)
                .Nested<Review, ReviewDto>(Maps.DeclareReview));
        }
    }

    /// <summary>The same closed profile type, declaring one of the three navigations.</summary>
    private sealed class OneNavigationProfile : MappedEntitySetProfile<int, ProductDto, Product>
    {
        public OneNavigationProfile() : base(d => d.Id)
        {
            EntitySetName = "One668";
            UseMap(Empty, m => m
                .Root(r =>
                {
                    r.Property(d => d.Id).From(o => o.Id);
                    r.Property(d => d.Title).From(o => o.Name);
                    r.Property(d => d.CategoryName).From(o => o.Category!.Name);
                    r.Property(d => d.DisplayName).Format(o => $"{o.First} {o.Last}");
                    r.Property(d => d.Rank).From(o => o.Rank);
                    r.Collection(d => d.Reviews).From(o => o.Reviews).AsIs();
                    r.Ignore(d => d.Category);
                    r.Ignore(d => d.Tags);
                    r.Ignore(d => d.RenderedAt);
                })
                .Nested<Review, ReviewDto>(Maps.DeclareReview));
        }
    }

    [Fact]
    public void EachNavigation_IsRegisteredWithItsOwnElementModelType()
    {
        Assert.Equal(
            new[] { "Category:single:-", "Reviews:many:ReviewDto", "Tags:many:TagDto" },
            Describe(new AllNavigationsProfile()));
    }

    [Fact]
    public void ASecondConstruction_RegistersTheSameNavigations()
    {
        string[] first = Describe(new AllNavigationsProfile());

        // The second takes the memoised registrars. A registrar carrying anything of the first
        // instance would show up here, since every one of them is invoked again.
        Assert.Equal(first, Describe(new AllNavigationsProfile()));
    }

    [Fact]
    public void ASiblingProfileOverTheSameModel_RegistersOnlyItsOwnNavigations()
    {
        _ = new AllNavigationsProfile();

        // Same closed generic, so the same registrar cache -- but the declaration decides which
        // registrars run.
        Assert.Equal(new[] { "Reviews:many:ReviewDto" }, Describe(new OneNavigationProfile()));
    }

    private static string[] Describe(EntitySetProfile<int, ProductDto> profile) =>
        ((IEntitySetEndpointSource)profile).NavigationRoutes
            .Select(route =>
                $"{route.PropertyName}:{(route.IsCollection ? "many" : "single")}:" +
                $"{route.NavItemType?.Name ?? "-"}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();
}
