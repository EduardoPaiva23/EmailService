using FluentValidation;

namespace EmailService.Application.UseCases.SendEmail;

public sealed class SendEmailCommandValidator : AbstractValidator<SendEmailCommand>
{
    public const int MaxIdempotencyKeyLength = 100;
    public const int MaxRecipientLength = 320;
    public const int MaxSubjectLength = 998;   // limite de linha da RFC 5322
    public const int MaxBodyLength = 1_000_000; // ~1 MB; anexos estão fora do MVP

    public SendEmailCommandValidator()
    {
        RuleFor(c => c.IdempotencyKey)
            .NotEmpty().WithMessage("O header Idempotency-Key é obrigatório.")
            .MaximumLength(MaxIdempotencyKeyLength);

        RuleFor(c => c.To)
            .NotEmpty()
            .MaximumLength(MaxRecipientLength)
            .EmailAddress();

        RuleFor(c => c.Subject)
            .NotEmpty()
            .MaximumLength(MaxSubjectLength)
            // Quebra de linha no assunto permitiria injetar cabeçalhos (ex.: Bcc).
            .Must(s => s is null || !s.Contains('\r') && !s.Contains('\n'))
            .WithMessage("O assunto não pode conter quebras de linha.");

        RuleFor(c => c.Body)
            .NotEmpty()
            .MaximumLength(MaxBodyLength);
    }
}
