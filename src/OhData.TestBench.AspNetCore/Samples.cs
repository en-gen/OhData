using OhData.Abstractions;

namespace OhData.TestBench.AspNetCore;

public class Parent
{
    public Guid Id { get; set; }

    public Parent Peer { get; set; }

    public IEnumerable<Child> Children { get; set; }
}

public class ParentEntitySetProfile : EntitySetProfile<Guid, Parent>
{
    public ParentEntitySetProfile() : base(x => x.Id)
    {
        SelectEnabled = true;
        FilterEnabled = true;
        ExpandEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;

        HasOptional(x => x.Peer);
        HasMany(x => x.Children);

        BindFunction(ShouldNotHaveSideEffects);
        BindAction(HasSideEffects);
    }

    private async Task<IEnumerable<Parent>> ShouldNotHaveSideEffects(string p1, int p2, long p3)
    {
        return Array.Empty<Parent>();
    }

    private void HasSideEffects(){}
        
}

public class Child
{
    public Guid Id { get; set; }

    public Parent Parent { get; set; }

    public IEnumerable<Child> Siblings { get; set; }
}

public class ChildEntitySetProfile : EntitySetProfile<Guid, Child>
{
    public ChildEntitySetProfile() : base(x => x.Id)
    {
        SelectEnabled = true;
        FilterEnabled = true;
        ExpandEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;

        HasRequired(x => x.Parent);
        HasMany(x => x.Siblings);
    }
}