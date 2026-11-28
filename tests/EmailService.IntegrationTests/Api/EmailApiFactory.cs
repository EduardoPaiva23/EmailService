using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EmailService.IntegrationTests.Api;

/// <summary>Sobe a API real em memória apontando para o SQL Server do Testcontainers.</summary>
public sealed class EmailApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Fora de Development: não roda o seed nem lê User Secrets.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:EmailServiceDb", connectionString);
    }
}
