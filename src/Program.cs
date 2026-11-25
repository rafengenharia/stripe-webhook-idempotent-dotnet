using Microsoft.EntityFrameworkCore;
using Stripe;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDb>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=app.db"));
builder.Services.AddScoped<IEventHandler, InvoiceEventHandler>();

var app = builder.Build();
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AppDb>().Database.EnsureCreated();

app.MapPost("/stripe/webhook", async (HttpRequest req, AppDb db, IEventHandler handler, IConfiguration cfg) =>
{
    // 1. Read the RAW body. The signature is computed over the exact bytes Stripe sent.
    var json = await new StreamReader(req.Body).ReadToEndAsync();

    // 2. Verify the signature. Throws StripeException if it is invalid.
    Event stripeEvent;
    try
    {
        stripeEvent = EventUtility.ConstructEvent(
            json, req.Headers["Stripe-Signature"], cfg["Stripe:WebhookSecret"],
            throwOnApiVersionMismatch: false);
    }
    catch (StripeException)
    {
        return Results.BadRequest();
    }

    // 3. Fast path: already processed this event id.
    if (await db.ProcessedEvents.AnyAsync(e => e.Id == stripeEvent.Id))
        return Results.Ok();

    // 4. Do the work and record the event id in ONE transaction.
    await using var tx = await db.Database.BeginTransactionAsync();
    try
    {
        db.ProcessedEvents.Add(new ProcessedEvent { Id = stripeEvent.Id, Type = stripeEvent.Type, At = DateTime.UtcNow });
        await handler.HandleAsync(stripeEvent);
        await db.SaveChangesAsync();   // PK violation here means a concurrent delivery won the race
        await tx.CommitAsync();
    }
    catch (DbUpdateException)
    {
        db.ChangeTracker.Clear();
        if (await db.ProcessedEvents.AsNoTracking().AnyAsync(e => e.Id == stripeEvent.Id))
            return Results.Ok();       // duplicate: a concurrent delivery won the race
        throw;                         // a real database error: 500 so Stripe retries
    }
    // Any other exception: transaction rolls back, we return 500, Stripe retries later.
    return Results.Ok();
});

app.Run();

public partial class Program { }

public class ProcessedEvent { public string Id { get; set; } = ""; public string Type { get; set; } = ""; public DateTime At { get; set; } }
public class Subscription { public string Id { get; set; } = ""; public string Status { get; set; } = ""; public int PaidInvoices { get; set; } }

public class AppDb(DbContextOptions<AppDb> o) : DbContext(o)
{
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    protected override void OnModelCreating(ModelBuilder b) => b.Entity<ProcessedEvent>().HasKey(e => e.Id);
}

public interface IEventHandler { Task HandleAsync(Event e); }

public class InvoiceEventHandler(AppDb db) : IEventHandler
{
    public async Task HandleAsync(Event e)
    {
        if (e.Type != EventTypes.InvoicePaid) return;   // ignore types we don't care about
        var invoice = (Invoice)e.Data.Object;
        var subId = invoice.CustomerId ?? "none";
        var sub = await db.Subscriptions.FindAsync(subId) ?? db.Subscriptions.Add(new Subscription { Id = subId }).Entity;
        sub.Status = "active";
        sub.PaidInvoices++;   // not idempotent on its own: that is why the event-id guard matters
    }
}
