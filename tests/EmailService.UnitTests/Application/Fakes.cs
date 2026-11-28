using EmailService.Application.Abstractions.Persistence;
using EmailService.Application.Exceptions;
using EmailService.Domain.Entities;

namespace EmailService.UnitTests.Application;

/// <summary>
/// Banco em memória para os testes de casos de uso. As alterações ficam
/// pendentes até SaveChangesAsync, que aplica tudo ou nada, como a
/// unidade de trabalho real.
/// </summary>
internal sealed class InMemoryStore : IEmailRepository, IOutboxRepository, IIdempotencyKeyRepository,
    IApiClientRepository, IUnitOfWork
{
    public List<Email> Emails { get; } = [];
    public List<EmailAttempt> Attempts { get; } = [];
    public List<OutboxMessage> Outbox { get; } = [];
    public List<IdempotencyKey> Keys { get; } = [];
    public List<ApiClient> Clients { get; } = [];
    public int SaveCount { get; private set; }

    /// <summary>Executado uma vez no início do próximo save; simula uma requisição concorrente.</summary>
    public Action? BeforeNextSave { get; set; }

    private readonly List<Email> _newEmails = [];
    private readonly List<EmailAttempt> _newAttempts = [];
    private readonly List<OutboxMessage> _newOutbox = [];
    private readonly List<IdempotencyKey> _newKeys = [];
    private readonly List<ApiClient> _newClients = [];

    void IEmailRepository.Add(Email email) => _newEmails.Add(email);
    void IEmailRepository.AddAttempt(EmailAttempt attempt) => _newAttempts.Add(attempt);
    Task<Email?> IEmailRepository.GetByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Emails.SingleOrDefault(e => e.Id == id));

    void IOutboxRepository.Add(OutboxMessage message) => _newOutbox.Add(message);

    void IIdempotencyKeyRepository.Add(IdempotencyKey key) => _newKeys.Add(key);
    // As instâncias de Keys são as "do banco"; a alteração já está aplicada nelas.
    void IIdempotencyKeyRepository.Update(IdempotencyKey key) { }
    Task<IdempotencyKey?> IIdempotencyKeyRepository.GetAsync(Guid clientId, string key, CancellationToken ct) =>
        Task.FromResult(Keys.SingleOrDefault(k => k.ClientId == clientId && k.Key == key));

    void IApiClientRepository.Add(ApiClient client) => _newClients.Add(client);
    Task<ApiClient?> IApiClientRepository.GetByKeyPrefixAsync(string keyPrefix, CancellationToken ct) =>
        Task.FromResult(Clients.SingleOrDefault(c => c.KeyPrefix == keyPrefix));

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var concurrent = BeforeNextSave;
        BeforeNextSave = null;
        concurrent?.Invoke();

        try
        {
            // Mesma regra da PK (ClientId, Key) no banco.
            if (_newKeys.Any(n => Keys.Any(k => k.ClientId == n.ClientId && k.Key == n.Key)))
                throw new DuplicateKeyException("Chave duplicada (simulada).", new InvalidOperationException());

            Emails.AddRange(_newEmails);
            Attempts.AddRange(_newAttempts);
            Outbox.AddRange(_newOutbox);
            Keys.AddRange(_newKeys);
            Clients.AddRange(_newClients);
            SaveCount++;
            return Task.CompletedTask;
        }
        finally
        {
            // Como o EF após falha num contexto descartado: nada pendente sobrevive.
            _newEmails.Clear();
            _newAttempts.Clear();
            _newOutbox.Clear();
            _newKeys.Clear();
            _newClients.Clear();
        }
    }
}
