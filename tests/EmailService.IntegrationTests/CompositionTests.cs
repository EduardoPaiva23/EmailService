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
            .AddApplication()
            .AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.NotNull(provider);
    }
}
