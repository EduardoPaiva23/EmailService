using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EmailService.Application.Messaging;
using EmailService.Infrastructure.Persistence;
using EmailService.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailService.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class EmailsApiTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private EmailApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new EmailApiFactory(fixture.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static object ValidBody(string subject = "Bem-vindo!") => new
    {
        to = "cliente@example.com",
        subject,
        body = "<h1>Olá!</h1>",
        isHtml = true
    };

    private HttpClient Client(string? apiKey)
    {
        var client = _factory.CreateClient();
        if (apiKey is not null)
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private static HttpRequestMessage Post(object body, string? idempotencyKey = "req-1", string? correlationId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/emails") { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (correlationId is not null) request.Headers.Add("X-Correlation-Id", correlationId);
        return request;
    }

    private static async Task<ProblemDetails> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
    }

    private sealed record Accepted(Guid Id, string Status, DateTime CreatedAt);

    // ---------- Autenticação ----------

    [Fact]
    public async Task Post_WithoutApiKey_Returns401()
    {
        var response = await Client(apiKey: null).SendAsync(Post(ValidBody()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(401, (await ReadProblemAsync(response)).Status);
    }

    [Fact]
    public async Task Post_WithInvalidApiKey_Returns401()
    {
        var (_, apiKey) = await fixture.CreateClientWithKeyAsync();
        var forged = apiKey[..^4] + "xxxx";

        var response = await Client(forged).SendAsync(Post(ValidBody()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithoutApiKey_Returns401()
    {
        var response = await Client(apiKey: null).GetAsync($"/api/v1/emails/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- Validação ----------

    [Fact]
    public async Task Post_WithoutIdempotencyKey_Returns400()
    {
        var (_, apiKey) = await fixture.CreateClientWithKeyAsync();

        var response = await Client(apiKey).SendAsync(Post(ValidBody(), idempotencyKey: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Contains("Idempotency-Key", JsonSerializer.Serialize(problem.Extensions));
    }

    [Fact]
    public async Task Post_WithInvalidPayload_Returns400WithFieldErrors()
    {
        var (_, apiKey) = await fixture.CreateClientWithKeyAsync();
        var body = new { to = "sem-arroba", subject = "", body = "x", isHtml = false };

        var response = await Client(apiKey).SendAsync(Post(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains("to", problem!.Errors.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("subject", problem.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_WithMalformedJson_Returns400()
    {
        var (_, apiKey) = await fixture.CreateClientWithKeyAsync();
        var request = Post(ValidBody());
        request.Content = new StringContent("{ isto não é json", Encoding.UTF8, "application/json");

        var response = await Client(apiKey).SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ReadProblemAsync(response);
    }

    [Fact]
    public async Task Post_WithNonGuidCorrelationId_Returns400()
    {
        var (_, apiKey) = await fixture.CreateClientWithKeyAsync();

        var response = await Client(apiKey).SendAsync(Post(ValidBody(), correlationId: "nao-e-guid"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- Criação ----------

    [Fact]
    public async Task Post_Valid_Returns202AndPersistsPendingEmailWithOutbox()
    {
        var (client, apiKey) = await fixture.CreateClientWithKeyAsync();

        var response = await Client(apiKey).SendAsync(Post(ValidBody()));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Accepted>();
        Assert.Equal("Pending", body!.Status);
        Assert.Equal($"/api/v1/emails/{body.Id}", response.Headers.Location?.OriginalString);

        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = await db.Emails.SingleAsync(e => e.Id == body.Id);
        Assert.Equal(client.Id, email.ClientId);

        var outbox = await db.OutboxMessages.Where(o => o.Type == SendEmailMessage.Type).ToListAsync();
        Assert.Contains(outbox, o => o.Payload.Contains(body.Id.ToString()));
        Assert.True(await db.IdempotencyKeys.AnyAsync(k => k.ClientId == client.Id && k.Key == "req-1"));
    }

    [Fact]
    public async Task Post_SameKeyAndPayload_ReturnsOriginalWithoutDuplicating()
    {
        var (client, apiKey) = await fixture.CreateClientWithKeyAsync();
        var http = Client(apiKey);

        var first = await http.SendAsync(Post(ValidBody()));
        var second = await http.SendAsync(Post(ValidBody()));

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<Accepted>();
        var b = await second.Content.ReadFromJsonAsync<Accepted>();
        Assert.Equal(a!.Id, b!.Id);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));

        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Emails.CountAsync(e => e.ClientId == client.Id));
    }

    [Fact]
    public async Task Post_ConcurrentSameKey_CreatesOnlyOneEmail()
    {
        var (client, apiKey) = await fixture.CreateClientWithKeyAsync();
        var http = Client(apiKey);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => http.SendAsync(Post(ValidBody(), "req-paralela"))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<Accepted>()));
        Assert.Single(ids.Select(i => i!.Id).Distinct());

        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Emails.CountAsync(e => e.ClientId == client.Id));
    }

    [Fact]
    public async Task Post_SameKeyWithDifferentPayload_Returns422()
    {
        var (_, apiKey) = await fixture.CreateClientWithKeyAsync();
        var http = Client(apiKey);
        await http.SendAsync(Post(ValidBody("Original")));

        var response = await http.SendAsync(Post(ValidBody("Outro assunto")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(422, (await ReadProblemAsync(response)).Status);
    }

    // ---------- CorrelationId ----------

    [Fact]
    public async Task Post_WithoutCorrelationId_GeneratesAndReturnsIt()
    {
        var (_, apiKey) = await fixture.CreateClientWithKeyAsync();

        var response = await Client(apiKey).SendAsync(Post(ValidBody()));

        var header = Assert.Single(response.Headers.GetValues("X-Correlation-Id"));
        var correlationId = Guid.Parse(header);
        var body = await response.Content.ReadFromJsonAsync<Accepted>();

        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(correlationId, (await db.Emails.SingleAsync(e => e.Id == body!.Id)).CorrelationId);
    }

    [Fact]
    public async Task Post_WithCorrelationId_EchoesAndStoresIt()
    {
        var (_, apiKey) = await fixture.CreateClientWithKeyAsync();
        var correlationId = Guid.NewGuid();

        var response = await Client(apiKey).SendAsync(Post(ValidBody(), correlationId: correlationId.ToString()));

        Assert.Equal(correlationId.ToString(), Assert.Single(response.Headers.GetValues("X-Correlation-Id")));
        var body = await response.Content.ReadFromJsonAsync<Accepted>();

        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(correlationId, (await db.Emails.SingleAsync(e => e.Id == body!.Id)).CorrelationId);
    }

    // ---------- Consulta ----------

    [Fact]
    public async Task Get_OwnEmail_Returns200WithStatus()
    {
        var (_, apiKey) = await fixture.CreateClientWithKeyAsync();
        var http = Client(apiKey);
        var created = await (await http.SendAsync(Post(ValidBody()))).Content.ReadFromJsonAsync<Accepted>();

        var response = await http.GetAsync($"/api/v1/emails/{created!.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(created.Id, json.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Pending", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("cliente@example.com", json.RootElement.GetProperty("to").GetString());
    }

    [Fact]
    public async Task Get_EmailOfAnotherClient_Returns404()
    {
        var (_, ownerKey) = await fixture.CreateClientWithKeyAsync();
        var (_, otherKey) = await fixture.CreateClientWithKeyAsync();
        var created = await (await Client(ownerKey).SendAsync(Post(ValidBody()))).Content.ReadFromJsonAsync<Accepted>();

        var response = await Client(otherKey).GetAsync($"/api/v1/emails/{created!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, (await ReadProblemAsync(response)).Status);
    }

    [Fact]
    public async Task Get_UnknownEmail_Returns404()
    {
        var (_, apiKey) = await fixture.CreateClientWithKeyAsync();

        var response = await Client(apiKey).GetAsync($"/api/v1/emails/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
