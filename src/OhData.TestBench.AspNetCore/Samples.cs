using OhData.Abstractions;

namespace OhData.TestBench.AspNetCore;

public class Parent
{
    public Guid Id { get; set; }

    public Parent? Peer { get; set; }

    public IEnumerable<Child>? Children { get; set; }
}

public class ParentEntitySetProfile : EntitySetProfile<Guid, Parent>
{
    // In-memory store for demo purposes
    private static readonly List<Parent> Store = new()
    {
        new Parent { Id = Guid.Parse("00000000-0000-0000-0000-000000000001") },
        new Parent { Id = Guid.Parse("00000000-0000-0000-0000-000000000002") },
    };

    public ParentEntitySetProfile() : base(x => x.Id)
    {
        SelectEnabled = true;
        FilterEnabled = true;
        ExpandEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;

        HasOptional(x => x.Peer!);
        HasMany(x => x.Children!);

        BindFunction(ShouldNotHaveSideEffects);
        BindAction(HasSideEffects);

        GetAll = (ct) => Task.FromResult<IEnumerable<Parent>>(Store);

        GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(p => p.Id == id));

        Post = (parent, ct) =>
        {
            parent.Id = Guid.NewGuid();
            Store.Add(parent);
            return Task.FromResult(parent);
        };

        PutById = (id, parent, ct) =>
        {
            Store.RemoveAll(p => p.Id == id);
            parent.Id = id;
            Store.Add(parent);
            return Task.FromResult(parent);
        };

        Delete = (id, ct) => Task.FromResult(Store.RemoveAll(p => p.Id == id) > 0);

        Patch = (id, parent, ct) =>
        {
            var existing = Store.FirstOrDefault(p => p.Id == id);
            return Task.FromResult(existing);
        };
    }

    private Task<IEnumerable<Parent>> ShouldNotHaveSideEffects(string p1, int p2, long p3) =>
        Task.FromResult(Enumerable.Empty<Parent>());

    private void HasSideEffects() { }
}

public class Child
{
    public Guid Id { get; set; }

    public Parent? Parent { get; set; }

    public IEnumerable<Child>? Siblings { get; set; }
}

public class ChildEntitySetProfile : EntitySetProfile<Guid, Child>
{
    private static readonly List<Child> Store = new();

    public ChildEntitySetProfile() : base(x => x.Id)
    {
        SelectEnabled = true;
        FilterEnabled = true;
        ExpandEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;

        HasRequired(x => x.Parent!);
        HasMany(x => x.Siblings!);

        RequireAuthorization();

        GetAll = (ct) => Task.FromResult<IEnumerable<Child>>(Store);

        GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(c => c.Id == id));

        Post = (child, ct) =>
        {
            child.Id = Guid.NewGuid();
            Store.Add(child);
            return Task.FromResult(child);
        };

        Delete = (id, ct) => Task.FromResult(Store.RemoveAll(c => c.Id == id) > 0);

        Patch = (id, child, ct) =>
        {
            var existing = Store.FirstOrDefault(c => c.Id == id);
            return Task.FromResult(existing);
        };
    }
}
