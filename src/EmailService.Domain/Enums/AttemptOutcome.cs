namespace EmailService.Domain.Enums;

public enum AttemptOutcome
{
    Success = 1,
    TransientError = 2,
    PermanentError = 3,

    /// <summary>Resultado desconhecido: conexão perdida após o DATA ou Worker caiu no meio do envio.</summary>
    Unknown = 4
}
