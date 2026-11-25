namespace EmailService.Application.Exceptions;

/// <summary>
/// Um registro foi alterado por outro processo entre a leitura e a gravação
/// (concorrência otimista por RowVersion).
/// </summary>
public sealed class ConcurrencyConflictException(string message, Exception innerException)
    : Exception(message, innerException);
