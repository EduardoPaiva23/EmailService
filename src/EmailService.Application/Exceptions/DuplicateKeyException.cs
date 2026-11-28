namespace EmailService.Application.Exceptions;

/// <summary>
/// A gravação violou uma chave primária ou um índice único, por exemplo, duas
/// requisições simultâneas com a mesma Idempotency-Key.
/// </summary>
public sealed class DuplicateKeyException(string message, Exception innerException)
    : Exception(message, innerException);
