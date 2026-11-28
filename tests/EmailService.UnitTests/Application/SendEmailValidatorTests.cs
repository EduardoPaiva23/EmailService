using EmailService.Application.UseCases.SendEmail;

namespace EmailService.UnitTests.Application;

public class SendEmailValidatorTests
{
    private readonly SendEmailCommandValidator _validator = new();

    private static SendEmailCommand Valid() =>
        new(Guid.NewGuid(), "req-1", Guid.NewGuid(), "destino@example.com", "Assunto", "Corpo", false);

    [Fact]
    public void ValidCommand_Passes()
    {
        Assert.True(_validator.Validate(Valid()).IsValid);
    }

    public static TheoryData<string, SendEmailCommand> InvalidCommands => new()
    {
        { "IdempotencyKey", Valid() with { IdempotencyKey = "" } },
        { "IdempotencyKey", Valid() with { IdempotencyKey = new string('k', SendEmailCommandValidator.MaxIdempotencyKeyLength + 1) } },
        { "To", Valid() with { To = "" } },
        { "To", Valid() with { To = "sem-arroba" } },
        { "To", Valid() with { To = new string('a', 310) + "@example.com" } },
        { "Subject", Valid() with { Subject = " " } },
        { "Subject", Valid() with { Subject = new string('s', SendEmailCommandValidator.MaxSubjectLength + 1) } },
        { "Subject", Valid() with { Subject = "Linha 1\r\nBcc: invasor@example.com" } },
        { "Body", Valid() with { Body = "" } },
        { "Body", Valid() with { Body = new string('b', SendEmailCommandValidator.MaxBodyLength + 1) } },
    };

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public void InvalidCommand_FailsOnExpectedProperty(string property, SendEmailCommand command)
    {
        var result = _validator.Validate(command);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == property);
    }
}
