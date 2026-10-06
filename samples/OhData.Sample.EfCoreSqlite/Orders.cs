using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OhData;
using OhData.AspNetCore.Mapper;

namespace OhData.Sample.EfCoreSqlite;

// ── API model ≠ entity ────────────────────────────────────────────────────────
//
// Order is shaped for storage; OrderDto is shaped for the wire. The two differ in a name
// (Number / OrderNumber), a value that lives on another table (Customer.Name), and a value the
// entity stores as two columns (ShipFirst + ShipLast -> ShipTo). EnGen.OhData.AspNetCore.Mapper
// lets you declare those correspondences once, and serves reads (OrderProfile) and writes
// (OrderDeltaProfile) from them.

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class Order
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string ShipFirst { get; set; } = "";
    public string ShipLast { get; set; } = "";
    public int TotalCents { get; set; }
}

public class CustomerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class OrderDto
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = "";
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public string ShipFirst { get; set; } = "";
    public string ShipLast { get; set; } = "";
    public string ShipTo { get; set; } = "";
    public int TotalCents { get; set; }
    public CustomerDto? Customer { get; set; }
}

/// <summary>
/// The read half. You declare where each model member comes from; the mapper binds
/// <c>$filter</c>/<c>$orderby</c> against the model, rewrites them into entity terms, and EF
/// translates the result to SQL — <c>$filter=CustomerName eq 'Ada Lovelace'</c> becomes
/// <c>WHERE c.Name = …</c> over the join the <c>CustomerName</c> projection already carries, and
/// <c>$expand=Customer</c> is served by one batched query per page.
/// </summary>
public sealed class OrderProfile : MappedEntitySetProfile<int, OrderDto, Order>
{
    public OrderProfile(ShopDbContext db, IDeltaFactory deltas) : base(d => d.Id)
    {
        EntitySetName = "Orders";
        FilterEnabled = OrderByEnabled = SelectEnabled = ExpandEnabled = CountEnabled = true;

        UseMap(() => db.Orders.AsNoTracking(), m => m
            .Root(r =>
            {
                // Same-named members still need an explicit From: an unbound member is a startup error.
                r.Property(d => d.Id).From(o => o.Id);
                r.Property(d => d.OrderNumber).From(o => o.Number);                  // rename
                r.Property(d => d.CustomerId).From(o => o.CustomerId);
                r.Property(d => d.CustomerName).From(o => o.Customer.Name);          // path (a JOIN)
                r.Property(d => d.ShipFirst).From(o => o.ShipFirst);
                r.Property(d => d.ShipLast).From(o => o.ShipLast);
                r.Property(d => d.ShipTo).Format(o => $"{o.ShipFirst} {o.ShipLast}"); // format
                r.Property(d => d.TotalCents).From(o => o.TotalCents);
                r.Reference(d => d.Customer, o => o.Customer);                       // $expand=Customer
            })
            .Nested<Customer, CustomerDto>(c =>
            {
                c.Property(d => d.Id).From(o => o.Id);
                c.Property(d => d.Name).From(o => o.Name);
            }));

        // The write half. The handlers own persistence; IDeltaFactory only translates the DTO's
        // change set into an entity change set. Responses are read back through the mapped
        // GetById, so they carry the same derived members (CustomerName, ShipTo) as a GET.
        // Without this, a dangling CustomerId would surface as a SQLite FK violation (a 500).
        async Task<OhDataResult?> RejectUnknownCustomer(Order order, CancellationToken ct) =>
            await db.Customers.AnyAsync(c => c.Id == order.CustomerId, ct)
                ? null
                : OhDataResult.BadRequest("UnknownCustomer", $"Customer {order.CustomerId} does not exist.", "CustomerId");

        Post = async (dto, ct) =>
        {
            var order = new Order();
            deltas.Create<OrderDto, Order>(dto).Patch(order);
            if (await RejectUnknownCustomer(order, ct) is { } rejection) return rejection;
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);
            OrderDto created = (await GetById!(order.Id, ct)).Value!;
            return OhDataResult.Success(created);
        };

        Put = async (id, dto, ct) =>
        {
            Order? order = await db.Orders.FindAsync([id], ct);
            if (order is null) return OhDataResult.Success<OrderDto?>(null); // -> 404
            deltas.Create<OrderDto, Order>(dto).Patch(order);
            if (await RejectUnknownCustomer(order, ct) is { } rejection) return rejection;
            await db.SaveChangesAsync(ct);
            return await GetById!(id, ct);
        };

        Patch = async (id, delta, ct) =>
        {
            Order? order = await db.Orders.FindAsync([id], ct);
            if (order is null) return OhDataResult.Success<OrderDto?>(null); // -> 404
            deltas.Create<OrderDto, Order>(delta).Patch(order); // only what the client sent
            if (await RejectUnknownCustomer(order, ct) is { } rejection) return rejection;
            await db.SaveChangesAsync(ct);
            return await GetById!(id, ct);
        };
    }
}

/// <summary>
/// Declares only the divergences between <see cref="OrderDto"/> and <see cref="Order"/>; every
/// other property is matched by name. Validated once at startup — an unmapped property is an
/// error, not a silent drop.
/// </summary>
public sealed class OrderDeltaProfile : DeltaProfile
{
    public OrderDeltaProfile()
    {
        For<OrderDto, Order>()
            .Rename(d => d.OrderNumber, e => e.Number)
            .Ignore(d => d.Id)           // the key is never client-writable
            .Ignore(d => d.Customer)     // a navigation: the client re-points it through CustomerId
            .Ignore(d => d.CustomerName) // derived on read; no entity column; a write to it is ignored
            .Ignore(d => d.ShipTo);      // derived on read; a write to it is ignored (PATCH {"ShipTo":...} -> 200, no change)
    }
}
