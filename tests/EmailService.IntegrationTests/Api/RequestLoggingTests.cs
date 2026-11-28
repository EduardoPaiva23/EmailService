using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using EmailService.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace EmailService.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class RequestLoggingTests(SqlServerFixture fixture)
{
    private sealed class CapturingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    [Fact]
    public async Task HandledError_IsLoggedWithFinalStatusAndCorrelationId()
    {
        var sink = new CapturingSink();
        await using var factory = new EmailApiFactory(fixture.ConnectionString).WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<ILogEventSink>(sink)));
        var (_, apiKey) = await fixture.CreateClientWithKeyAsync();
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        HttpRequestMessage Post(string subject)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emails")
            {
                Content = JsonContent.Create(new { to = "a@example.com", subject, body = "b", isHtml = false })
            };
            r.Headers.Add("Idempotency-Key", "req-log");
            return r;
        }

        await http.SendAsync(Post("Original"));
        var conflict = await http.SendAsync(Post("Diferente"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);
        var correlationId = Assert.Single(conflict.Headers.GetValues("X-Correlation-Id"));

        var requestLog = Assert.Single(sink.Events, e =>
            e.Properties.TryGetValue("RequestPath", out var path) && path.ToString().Contains("/api/v1/emails") &&
            e.Properties.TryGetValue("CorrelationId", out var c) && c.ToString().Trim('"') == correlationId);

        Assert.Equal("422", requestLog.Properties["StatusCode"].ToString());
        Assert.Equal(LogEventLevel.Information, requestLog.Level);
    }
}
