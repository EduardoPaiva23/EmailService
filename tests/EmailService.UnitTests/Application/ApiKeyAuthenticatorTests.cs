using EmailService.Application.Security;
using EmailService.Domain.Entities;

namespace EmailService.UnitTests.Application;

public class ApiKeyAuthenticatorTests
{
    private const string Key = "es_test_0123456789abcdefghijklmnopqrstuvwxyz";
    private readonly InMemoryStore _store = new();

    private ApiClient SeedClient(string key = Key)
    {
        var client = ApiClient.Create("Cliente", ApiKeyHasher.GetPrefix(key), ApiKeyHasher.Hash(key), DateTime.UtcNow);
        _store.Clients.Add(client);
        return client;
    }

    private ApiKeyAuthenticator Authenticator() => new(_store);

    [Fact]
    public async Task ValidKey_ReturnsClientId()
    {
        var client = SeedClient();

        Assert.Equal(client.Id, await Authenticator().AuthenticateAsync(Key));
    }

    [Fact]
    public async Task SamePrefixWrongSecret_ReturnsNull()
    {
        SeedClient();
        var forged = Key[..ApiKeyHasher.PrefixLength] + new string('x', Key.Length - ApiKeyHasher.PrefixLength);

        Assert.Null(await Authenticator().AuthenticateAsync(forged));
    }

    [Fact]
    public async Task UnknownPrefix_ReturnsNull()
    {
        SeedClient();

        Assert.Null(await Authenticator().AuthenticateAsync("es_other_0123456789abcdefghijklmnopqrstuvwxyz"));
    }

    [Fact]
    public async Task InactiveClient_ReturnsNull()
    {
        SeedClient().Deactivate();

        Assert.Null(await Authenticator().AuthenticateAsync(Key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("curta")]
    public async Task MissingOrShortKey_ReturnsNull(string? key)
    {
        SeedClient();

        Assert.Null(await Authenticator().AuthenticateAsync(key));
    }
}
