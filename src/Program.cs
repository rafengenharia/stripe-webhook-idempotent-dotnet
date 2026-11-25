using Microsoft.EntityFrameworkCore;
using Stripe;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDb>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=app.db"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IEventHandler, InvoiceEventHandler>();

var app = builder.Build();
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<AppDb>().Database.EnsureCreated();

app.MapPost("/stripe/webhook", async (HttpRequest req, AppDb db, IEventHandler handler,
    IConfiguration cfg, TimeProvider clock, ILogger<Program> log) =>
{
    var secret = cfg["Stripe:WebhookSecret"];
    if (string.IsNullOrEmpty(secret))
    {
        log.LogError("Stripe:WebhookSecret is not configured");
        return Results.StatusCode(500);
    }

    // 1. Read the RAW body. The signature is computed over the exact bytes Stripe sent.
    var json = await new StreamReader(req.Body).ReadToEndAsync();

    // 2. Verify the signature (and the 5 minute timestamp tolerance). Throws StripeException if invalid.
    Event stripeEvent;
    try
    {
        stripeEvent = EventUtility.ConstructEvent(
            json, req.Headers["Stripe-Signature"], secret,
            throwOnApiVersionMismatch: false);
    }
    catch (StripeException)
    {
        log.LogWarning("Webhook signature verification failed");   // never log the body
        return Results.BadRequest();
    }

    // 3. Fast path: already processed this event id.
    if (await db.ProcessedEvents.AnyAsync(e => e.Id == stripeEvent.Id))
    {
        log.LogInformation("Webhook {EventId} already processed, ignoring", stripeEvent.Id);
        return Results.Ok();
    }

    // 4. Do the work and record the event id in ONE transaction.
    await using var tx = await db.Database.BeginTransactionAsync();
    try
    {
        db.ProcessedEvents.Add(new ProcessedEvent
        {
            Id = stripeEvent.Id,
            Type = stripeEvent.Type,
            At = clock.GetUtcNow()
        });
        await handler.HandleAsync(stripeEvent);
        await db.SaveChangesAsync();   // a primary key violation here means a concurrent delivery won the race
        await tx.CommitAsync();
        log.LogInformation("Webhook {EventId} {EventType} processed", stripeEvent.Id, stripeEvent.Type);
        return Results.Ok();
    }
    catch (DbUpdateException)
    {
        db.ChangeTracker.Clear();
        if (await db.ProcessedEvents.AsNoTracking().AnyAsync(e => e.Id == stripeEvent.Id))
            return Results.Ok();       // duplicate: a concurrent delivery won the race
        log.LogError("Webhook {EventId} failed with a database error", stripeEvent.Id);
        return Results.StatusCode(500);   // Stripe will retry
    }
    catch (Exception ex)
    {
        // The transaction rolls back when it is disposed. Return 500 so Stripe retries later.
        log.LogError("Webhook {EventId} failed: {ErrorType}", stripeEvent.Id, ex.GetType().Name);
        return Results.StatusCode(500);
    }
});

app.Run();

public partial class Program { }

public class ProcessedEvent { public string Id { get; set; } = ""; public string Type { get; set; } = ""; public DateTimeOffset At { get; set; } }
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
