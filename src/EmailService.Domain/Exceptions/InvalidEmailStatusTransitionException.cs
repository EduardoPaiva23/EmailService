using EmailService.Domain.Enums;

namespace EmailService.Domain.Exceptions;

public sealed class InvalidEmailStatusTransitionException(EmailStatus from, string operation)
    : DomainException($"Transição inválida: '{operation}' não é permitida a partir de '{from}'.")
{
    public EmailStatus From { get; } = from;
    public string Operation { get; } = operation;
}
