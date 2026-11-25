using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;

public class WebhookTests : IDisposable
{
    const string Secret = "whsec_test_secret";
    readonly string _db = Path.Combine(Path.GetTempPath(), $"wh-{Guid.NewGuid():N}.db");
    readonly WebApplicationFactory<Program> _factory;

    public WebhookTests() => _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        b.UseSetting("Stripe:WebhookSecret", Secret)
         .UseSetting("ConnectionStrings:Default", $"Data Source={_db}"));

    public void Dispose() { _factory.Dispose(); try { File.Delete(_db); } catch { } }

    static string EventJson(string id) => $$"""
    {"id":"{{id}}","object":"event","api_version":"2020-08-27","created":1700000000,"livemode":false,
     "type":"invoice.paid","data":{"object":{"id":"in_1","object":"invoice","customer":"cus_1"}}}
    """;

    static HttpRequestMessage Signed(string json, string secret = Secret)
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = Convert.ToHexString(new HMACSHA256(Encoding.UTF8.GetBytes(secret))
            .ComputeHash(Encoding.UTF8.GetBytes($"{t}.{json}"))).ToLowerInvariant();
        var req = new HttpRequestMessage(HttpMethod.Post, "/stripe/webhook") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Add("Stripe-Signature", $"t={t},v1={sig}");
        return req;
    }

    int PaidInvoices()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDb>().Subscriptions.Single(s => s.Id == "cus_1").PaidInvoices;
    }

    [Fact]
    public async Task Replayed_event_is_processed_once()
    {
        var c = _factory.CreateClient();
        var json = EventJson("evt_1");
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Signed(json))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Signed(json))).StatusCode);
        Assert.Equal(1, PaidInvoices());
    }

    [Fact]
    public async Task Different_events_are_both_processed()
    {
        var c = _factory.CreateClient();
        await c.SendAsync(Signed(EventJson("evt_a")));
        await c.SendAsync(Signed(EventJson("evt_b")));
        Assert.Equal(2, PaidInvoices());
    }

    [Fact]
    public async Task Bad_signature_is_rejected()
    {
        var c = _factory.CreateClient();
        var r = await c.SendAsync(Signed(EventJson("evt_x"), "whsec_wrong"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
