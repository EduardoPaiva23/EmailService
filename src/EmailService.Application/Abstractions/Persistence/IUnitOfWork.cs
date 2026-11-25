namespace EmailService.Application.Abstractions.Persistence;

public interface IUnitOfWork
{
    /// <summary>
    /// Persiste todas as alterações pendentes em uma única transação.
    /// </summary>
    /// <exception cref="Exceptions.ConcurrencyConflictException">
    /// Outro processo alterou um registro depois que ele foi lido.
    /// </exception>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
