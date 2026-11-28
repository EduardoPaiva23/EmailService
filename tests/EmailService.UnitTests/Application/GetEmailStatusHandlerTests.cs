using EmailService.Application.UseCases.GetEmailStatus;
using EmailService.Domain.Entities;
using EmailService.Domain.Enums;

namespace EmailService.UnitTests.Application;

public class GetEmailStatusHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly InMemoryStore _store = new();

    private Email SeedEmail(Guid clientId)
    {
        var email = Email.Create(clientId, Guid.NewGuid(), "a@example.com", "Assunto", "Corpo", false, 5, Now);
        _store.Emails.Add(email);
        return email;
    }

    [Fact]
    public async Task OwnEmail_ReturnsStatus()
    {
        var clientId = Guid.NewGuid();
        var email = SeedEmail(clientId);

        var result = await new GetEmailStatusHandler(_store).HandleAsync(new GetEmailStatusQuery(clientId, email.Id));

        Assert.NotNull(result);
        Assert.Equal(email.Id, result.Id);
        Assert.Equal(EmailStatus.Pending, result.Status);
        Assert.Equal("a@example.com", result.To);
        Assert.Equal(0, result.Attempts);
        Assert.Equal(Now, result.CreatedAt);
    }

    [Fact]
    public async Task EmailOfAnotherClient_ReturnsNull()
    {
        var email = SeedEmail(Guid.NewGuid());

        var result = await new GetEmailStatusHandler(_store).HandleAsync(new GetEmailStatusQuery(Guid.NewGuid(), email.Id));

        Assert.Null(result);
    }

    [Fact]
    public async Task UnknownEmail_ReturnsNull()
    {
        var result = await new GetEmailStatusHandler(_store).HandleAsync(new GetEmailStatusQuery(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(result);
    }
}
