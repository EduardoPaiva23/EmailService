using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EmailService.Infrastructure.Persistence;

/// <summary>
/// Usado apenas pelas ferramentas do EF (<c>dotnet ef</c>). Gerar migrations
/// não precisa de banco; para <c>database update</c>, passe
/// <c>--connection</c> ou defina <c>ConnectionStrings__EmailServiceDb</c>.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__EmailServiceDb")
            ?? "Server=localhost,1433;Database=EmailService;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
        return new AppDbContext(options);
    }
}
