using EmailService.Application;
using Microsoft.Extensions.DependencyInjection;

namespace EmailService.UnitTests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddApplication_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddApplication();

        Assert.Same(services, result);
    }
}
