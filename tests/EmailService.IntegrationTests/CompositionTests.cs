using EmailService.Application;
using EmailService.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EmailService.IntegrationTests;

public class CompositionTests
{
    [Fact]
    public void ApplicationAndInfrastructure_ComposeIntoValidServiceProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services
            .AddLogging() // fornecido pelo Host na API e no Worker
            .AddApplication()
            .AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.NotNull(provider);
    }

    [Fact]
    public void ResolvingDbContext_WithoutConnectionString_FailsWithClearMessage()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddApplication()
            .AddInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ex = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<EmailService.Infrastructure.Persistence.AppDbContext>());

        Assert.Contains("EmailServiceDb", ex.Message);
    }
}
