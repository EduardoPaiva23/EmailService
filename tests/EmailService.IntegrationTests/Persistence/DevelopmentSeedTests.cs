using EmailService.Application.Security;
using EmailService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailService.IntegrationTests.Persistence;

[Collection(SqlServerCollection.Name)]
public class DevelopmentSeedTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Seed_CreatesClientWithHashedKey_AndIsIdempotent()
    {
        for (var run = 0; run < 2; run++)
        {
            await using var scope = fixture.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DevelopmentDataSeeder>().SeedAsync();
        }

        await using var verify = fixture.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var prefix = ApiKeyHasher.GetPrefix(SqlServerFixture.DevApiKey);

        var client = await db.ApiClients.SingleAsync(c => c.KeyPrefix == prefix);
        Assert.Equal("Cliente de teste", client.Name);
        Assert.Equal(ApiKeyHasher.Hash(SqlServerFixture.DevApiKey), client.KeyHash);
        Assert.True(client.IsActive);
        // A chave em texto puro nunca é gravada.
        Assert.DoesNotContain(SqlServerFixture.DevApiKey, client.KeyHash);
    }

    [Fact]
    public async Task Seed_WithoutConfiguredKey_DoesNothing()
    {
        await using var provider = SqlServerFixture.BuildProvider(new Dictionary<string, string?>
        {
            ["ConnectionStrings:EmailServiceDb"] = fixture.ConnectionString
        });

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = await db.ApiClients.CountAsync();

        await scope.ServiceProvider.GetRequiredService<DevelopmentDataSeeder>().SeedAsync();

        Assert.Equal(before, await db.ApiClients.CountAsync());
    }
}
