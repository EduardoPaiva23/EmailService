# EmailService --- Arquitetura e Plano de Implementação

> Documento inicial do projeto de serviço de envio de e-mails em C#/.NET
> 10.
>
> **Objetivo:** aprender arquitetura .NET enquanto desenvolve um serviço
> independente, escalável e preparado para alto volume.

## 1. Decisões do projeto

  Decisão                       Escolha
  ----------------------------- ------------------------------------------
  Linguagem                     C#
  Plataforma                    .NET 10
  Arquitetura de mensageria     RabbitMQ
  Banco de dados                SQL Server
  Provedor inicial              SMTP com MailKit
  Ambiente de desenvolvimento   Docker local
  Volume estimado               100.000 a 500.000 e-mails/dia
  Objetivo                      Aprender arquitetura enquanto implementa

O volume diário corresponde a uma média aproximada de 1,16 a 5,79
mensagens por segundo. A média não representa os picos: o sistema deve
suportar rajadas de solicitações, limites do provedor e acúmulo
temporário na fila.

### 1.1 Decisões complementares

Decisões tomadas para fechar lacunas do desenho inicial. Podem ser
revistas, mas mudá-las depois da etapa indicada gera retrabalho.

| #  | Tema                | Decisão                                                                                              | Etapa |
|----|---------------------|------------------------------------------------------------------------------------------------------|-------|
| D1 | Outbox Dispatcher   | Roda como `HostedService` dentro do Worker, reivindicando eventos por *lease* no SQL Server           | 4     |
| D2 | Retry               | O banco é a fonte da verdade. Reagendamento via Outbox (`AvailableAt`), sem filas de atraso no RabbitMQ | 7     |
| D3 | Idempotência da API | Header `Idempotency-Key` obrigatório desde a primeira versão da API                                   | 3     |
| D4 | Autenticação        | API key por cliente (somente o hash fica no banco), desde a primeira versão da API                    | 3     |
| D5 | Erros SMTP          | Classificação em temporário, permanente e ambíguo (seção 3)                                          | 6     |
| D6 | Anexos              | Fora do MVP. Ficam no backlog                                                                        | —     |
| D7 | Retenção de dados   | Limpeza periódica com prazos configuráveis (seção 5)                                                 | 2 e 10 |
| D8 | SMTP local          | Mailpit no Docker Compose para desenvolvimento e testes                                              | 1     |

## 2. Visão geral da arquitetura

A solução será um microsserviço independente, composto por uma API HTTP
e um ou mais processos Worker.

``` text
Sistemas clientes
       |
       v
EmailService.API
       |
       | grava e-mail + evento Outbox na mesma transação
       v
SQL Server
       |
       | Outbox Dispatcher (HostedService no Worker) publica evento
       v
RabbitMQ
       |
       | distribuição de mensagens
       v
EmailService.Worker (uma ou mais instâncias)
       |
       | busca dados do e-mail e processa
       v
SMTP / MailKit / Provedor de e-mail
       |
       v
SQL Server (status e tentativas)
```

### Responsabilidades

-   **API:** autenticar clientes por API key, validar solicitações,
    garantir idempotência, registrar o pedido e retornar
    `202 Accepted`. Não envia e-mails diretamente.
-   **Application:** orquestrar casos de uso e definir contratos para
    repositórios, mensageria e envio.
-   **Domain:** conter entidades, estados e regras de negócio, sem
    depender de infraestrutura.
-   **Infrastructure:** implementar acesso ao SQL Server, RabbitMQ, SMTP
    e armazenamento de anexos.
-   **Worker:** consumir mensagens, executar o envio, aplicar políticas
    de retry e atualizar o resultado. Também hospeda o Outbox
    Dispatcher, o job de recuperação de processamentos abandonados e o
    job de limpeza (retenção).

API e Worker são executáveis separados. Eles compartilham Application,
Domain e Infrastructure e podem ser escalados independentemente.

## 3. Fluxo de processamento

1.  Um cliente chama `POST /api/v1/emails` com os headers `X-Api-Key`
    e `Idempotency-Key`.
2.  A API autentica a chave e identifica o `ClientId`.
3.  A API valida a solicitação e consulta `IdempotencyKeys` pelo par
    `(ClientId, Idempotency-Key)`:
    -   chave já usada com o mesmo payload: devolve a resposta original
        (`202` com o mesmo `Id`), sem criar novo registro;
    -   chave já usada com payload diferente: `422 Unprocessable
        Entity`.
4.  Em uma única transação SQL, grava o e-mail em `Emails` (`Pending`),
    o evento em `OutboxMessages` (`AvailableAt` = agora) e a chave em
    `IdempotencyKeys`.
5.  O Outbox Dispatcher (no Worker) reivindica por *lease* um lote de
    eventos com `PublishedAt IS NULL` e `AvailableAt <= agora`, e os
    publica no RabbitMQ.
6.  O publicador usa mensagens persistentes e Publisher Confirms. O
    evento só é marcado como publicado (`PublishedAt`) após a
    confirmação do broker.
7.  Um Worker recebe a mensagem e carrega o e-mail no SQL Server. Se o
    status não for `Pending` (já enviado, cancelado ou em processamento
    por outra instância), envia ACK e descarta a mensagem.
8.  O Worker faz a transição condicional `Pending -> Processing`,
    definindo `LockedUntil` (UPDATE com `WHERE Status = Pending` e
    `RowVersion`). Se a atualização não afetar nenhuma linha, outra
    instância já assumiu: ACK e descarta. Em seguida registra a
    tentativa em `EmailAttempts` e chama o provedor.
9.  Se o provedor aceitar o envio, o Worker grava `Sent`, `SentAt` e o
    `ProviderMessageId`.
10. Em erro temporário ou ambíguo com tentativas restantes, o Worker,
    em uma única transação, volta o e-mail para `Pending`, incrementa
    `Attempts`, calcula `NextAttemptAt` (backoff com jitter) e grava um
    novo `OutboxMessage` com `AvailableAt = NextAttemptAt`. Em erro
    permanente ou ao esgotar as tentativas, marca `Failed` e grava
    `LastError`.
11. O consumidor envia ACK ao RabbitMQ somente depois de persistir o
    resultado do processamento.
12. Mensagens inválidas (não desserializam, e-mail inexistente) são
    rejeitadas sem requeue e vão para a DLQ.
13. Um job de recuperação devolve para `Pending` os e-mails em
    `Processing` com `LockedUntil` expirado (Worker caiu no meio do
    envio), registrando a tentativa com resultado `Unknown`.

`Failed` é um estado de negócio e fica no banco; a DLQ do RabbitMQ é
reservada para mensagens que não podem ser processadas (ver seção 6).

### Por que usar Transactional Outbox?

Sem Outbox, a API poderia salvar o e-mail no banco e falhar antes de
publicar a mensagem no RabbitMQ. O registro ficaria persistido, mas
nunca seria processado.

Com Outbox, o e-mail e o evento são gravados na mesma transação SQL. Um
processo independente publica os eventos pendentes, permitindo recuperar
falhas de publicação.

### Entrega e idempotência

O modelo deve ser tratado como **at-least-once**, não como exactly-once.
Uma mensagem pode ser entregue novamente se o Worker cair antes de
confirmar o processamento.

Também existe uma janela de duplicidade: o provedor pode aceitar o
e-mail e o Worker cair antes de gravar `Sent`. Na recuperação, o envio
pode ser repetido.

Medidas de mitigação:

-   Chave de idempotência na API para evitar solicitações duplicadas.
-   Processamento idempotente no Worker e transições de estado
    protegidas.
-   Registrar o identificador da mensagem retornado pelo provedor.
-   Usar recurso de idempotência do provedor, quando disponível.
-   Tratar resultados ambíguos de SMTP com cuidado; não assumir que toda
    falha de conexão significa que o provedor não aceitou a mensagem.

`Sent` significa que o provedor aceitou a mensagem para processamento,
não que ela chegou à caixa de entrada. Para acompanhar entregas,
rejeições e bounces, será necessária integração com webhooks/eventos do
provedor (backlog).

### Classificação de erros SMTP

O `IEmailSender` devolve um resultado classificado, e o Worker decide o
próximo estado a partir dessa classificação:

| Classificação | Exemplos                                                                                       | Tratamento                                              |
|---------------|------------------------------------------------------------------------------------------------|---------------------------------------------------------|
| Temporário    | Códigos `4xx`, timeout ou falha de conexão **antes** do envio do conteúdo (`DATA`), autenticação temporariamente indisponível | Retry com backoff                                       |
| Permanente    | Códigos `5xx` (destinatário inexistente, mensagem rejeitada), endereço inválido                 | `Failed` sem retry                                      |
| Ambíguo       | Conexão perdida **depois** do envio do conteúdo e antes da resposta do servidor                 | Registrar tentativa como `Unknown` e tratar como temporário (preferir duplicar a perder). Comportamento configurável |

A tabela é o ponto de partida. Os códigos devem ser revisados com as
respostas reais do provedor escolhido.

## 4. Estrutura da Solution

``` text
EmailService/
├── src/
│   ├── EmailService.API/
│   │   ├── Controllers/
│   │   ├── Middleware/
│   │   ├── Extensions/
│   │   └── Program.cs
│   │
│   ├── EmailService.Application/
│   │   ├── UseCases/
│   │   │   ├── SendEmail/
│   │   │   └── GetEmailStatus/
│   │   ├── Interfaces/
│   │   ├── DTOs/
│   │   └── Validators/
│   │
│   ├── EmailService.Domain/
│   │   ├── Entities/
│   │   │   ├── Email.cs
│   │   │   ├── EmailAttempt.cs
│   │   │   └── OutboxMessage.cs
│   │   ├── Enums/
│   │   └── Exceptions/
│   │
│   ├── EmailService.Infrastructure/
│   │   ├── Persistence/
│   │   │   ├── AppDbContext.cs
│   │   │   ├── Configurations/
│   │   │   ├── Repositories/
│   │   │   └── Migrations/
│   │   ├── Messaging/
│   │   ├── Email/
│   │   └── Storage/
│   │
│   └── EmailService.Worker/
│       ├── Consumers/
│       ├── Services/
│       ├── HostedServices/
│       ├── Configuration/
│       └── Program.cs
│
├── tests/
│   ├── EmailService.UnitTests/
│   └── EmailService.IntegrationTests/
├── docker-compose.yml
├── .env
├── .env.example
├── .gitignore
└── EmailService.sln
```

### Dependências entre projetos

``` text
API ───────────────> Application
API ───────────────> Infrastructure

Worker ────────────> Application
Worker ────────────> Infrastructure

Application ───────> Domain
Infrastructure ────> Application
Infrastructure ────> Domain
```

A camada Domain não deve depender de EF Core, RabbitMQ, MailKit ou
ASP.NET Core. API e Worker são os pontos de composição da aplicação:
registram as implementações concretas na injeção de dependência.

## 5. Modelo de dados inicial

| Tabela             | Finalidade                                              | Etapa   |
|--------------------|---------------------------------------------------------|---------|
| `Emails`           | Conteúdo, destinatário, status e dados do envio         | 2       |
| `EmailAttempts`    | Histórico de tentativas, duração e erros                | 2       |
| `OutboxMessages`   | Eventos aguardando publicação no RabbitMQ               | 2       |
| `IdempotencyKeys`  | Controle de solicitações repetidas                      | 2       |
| `ApiClients`       | Clientes autorizados e hash das API keys                | 2       |
| `EmailAttachments` | Metadados e localização dos anexos                      | Backlog |

### Campos de Emails

| Campo               | Observação                                                       |
|---------------------|------------------------------------------------------------------|
| `Id`                | `Guid`, gerado pela aplicação                                    |
| `ClientId`          | FK para `ApiClients`                                             |
| `CorrelationId`     | Propagado para logs, mensagens e tracing                         |
| `Recipient`         |                                                                  |
| `Subject`           |                                                                  |
| `Body`              | `nvarchar(max)`                                                  |
| `IsHtml`            |                                                                  |
| `Status`            | `EmailStatus`                                                    |
| `Attempts`          | Tentativas já realizadas                                         |
| `MaxAttempts`       | Padrão configurável (sugestão inicial: 5)                        |
| `CreatedAt`         | UTC                                                              |
| `UpdatedAt`         | UTC                                                              |
| `SentAt`            | UTC, preenchido em `Sent`                                        |
| `NextAttemptAt`     | Próxima tentativa agendada (informativo; o agendamento real é o `AvailableAt` da Outbox) |
| `LockedUntil`       | *Lease* do processamento; usado para recuperar `Processing` abandonado |
| `LastError`         | Resumo do último erro                                            |
| `ProviderMessageId` | Identificador devolvido pelo provedor                            |
| `RowVersion`        | `rowversion`, para concorrência otimista nas transições          |

### Campos de EmailAttempts

| Campo            | Observação                                                   |
|------------------|--------------------------------------------------------------|
| `Id`             |                                                              |
| `EmailId`        | FK para `Emails`                                             |
| `AttemptNumber`  |                                                              |
| `StartedAt`      | UTC                                                          |
| `FinishedAt`     | UTC                                                          |
| `DurationMs`     |                                                              |
| `Outcome`        | `Success`, `TransientError`, `PermanentError`, `Unknown`     |
| `SmtpStatusCode` | Quando houver                                                |
| `ErrorMessage`   |                                                              |
| `WorkerId`       | Instância que executou a tentativa                           |

### Campos de OutboxMessages

| Campo             | Observação                                                         |
|-------------------|--------------------------------------------------------------------|
| `Id`              |                                                                    |
| `Type`            | Ex.: `SendEmail`                                                   |
| `Payload`         | JSON do evento (`SendEmailMessage`)                                |
| `OccurredAt`      | UTC                                                                |
| `AvailableAt`     | A partir de quando pode ser publicado (usado para retry com atraso) |
| `PublishedAt`     | `NULL` enquanto pendente                                           |
| `PublishAttempts` | Falhas de publicação acumuladas                                    |
| `LastError`       |                                                                    |
| `LockedUntil`     | *Lease* do Dispatcher que reivindicou o evento                     |
| `LockedBy`        | Identificador da instância do Dispatcher                           |

O Dispatcher reivindica eventos com um `UPDATE TOP (@lote) ... OUTPUT`
que só seleciona linhas sem lease válido. Assim várias instâncias do
Worker podem rodar o Dispatcher sem publicar o mesmo evento duas vezes, e
nenhuma transação fica aberta durante a publicação.

### Campos de IdempotencyKeys

| Campo         | Observação                                                  |
|---------------|-------------------------------------------------------------|
| `ClientId`    | Chave primária composta com `Key`                           |
| `Key`         | Valor do header `Idempotency-Key`                           |
| `RequestHash` | Hash do payload, para detectar reutilização com outro conteúdo |
| `EmailId`     | E-mail criado pela solicitação original                     |
| `CreatedAt`   | UTC                                                         |
| `ExpiresAt`   | UTC (sugestão inicial: 24 h)                                |

### Campos de ApiClients

| Campo       | Observação                                                       |
|-------------|------------------------------------------------------------------|
| `Id`        |                                                                  |
| `Name`      |                                                                  |
| `KeyPrefix` | Primeiros caracteres da chave, para localizar o cliente e exibir |
| `KeyHash`   | SHA-256 da chave; a chave em texto puro nunca é armazenada       |
| `IsActive`  |                                                                  |
| `CreatedAt` | UTC                                                              |

### Índices iniciais

| Tabela            | Índice                                                         | Uso                                  |
|-------------------|----------------------------------------------------------------|--------------------------------------|
| `OutboxMessages`  | `(AvailableAt)` filtrado por `PublishedAt IS NULL`             | Busca do Dispatcher                  |
| `Emails`          | `(ClientId, CreatedAt DESC)`                                   | Histórico por cliente                |
| `Emails`          | `(LockedUntil)` filtrado por `Status = Processing`             | Job de recuperação                   |
| `EmailAttempts`   | `(EmailId)`                                                    | Detalhe de tentativas                |
| `IdempotencyKeys` | PK `(ClientId, Key)` + índice em `(ExpiresAt)`                 | Verificação e limpeza                |
| `ApiClients`      | Único em `(KeyPrefix)`                                         | Autenticação                         |

Os índices devem ser revisados com os planos de execução reais durante
os testes de carga.

### Retenção de dados

Com 100.000 a 500.000 e-mails por dia, `Emails` pode crescer até cerca
de 15 milhões de linhas por mês. Um job de limpeza no Worker remove
dados antigos em lotes pequenos, para não bloquear tabelas:

| Dado                         | Retenção sugerida (configurável)   |
|------------------------------|------------------------------------|
| `OutboxMessages` publicadas  | 7 dias                             |
| `IdempotencyKeys` expiradas  | Removidas após `ExpiresAt`         |
| `Emails` e `EmailAttempts`   | 90 dias                            |

Na Etapa 10, avaliar particionamento de `Emails` por `CreatedAt` e
arquivamento, conforme os requisitos de auditoria.

### Estados do e-mail

``` csharp
public enum EmailStatus
{
    Pending = 1,
    Processing = 2,
    Sent = 3,
    Failed = 4,
    Cancelled = 5
}
```

Transições principais:

``` text
Pending -> Processing -> Sent
                      -> Pending   (retry: erro temporário ou ambíguo)
                      -> Pending   (recuperação: LockedUntil expirado)
                      -> Failed    (erro permanente ou tentativas esgotadas)

Pending -> Cancelled               (cancelamento manual)
Failed  -> Pending                 (retry manual)
```

As transições ficam como métodos da entidade `Email` (por exemplo,
`StartProcessing`, `MarkSent`, `ScheduleRetry`, `MarkFailed`, `Cancel`),
que lançam exceção de domínio quando a transição é inválida. No banco, a
proteção contra concorrência usa UPDATE condicional por `Status` e
`RowVersion`.

Recuperação de `Processing` abandonado: ao assumir um e-mail, o Worker
define `LockedUntil` = agora + tempo máximo de envio (configurável). O
job de recuperação busca `Processing` com `LockedUntil` vencido, registra
a tentativa como `Unknown` e reagenda pelo mesmo caminho do retry (seção
3, passo 10).

### Mensagem RabbitMQ

Evitar colocar o corpo completo do e-mail ou anexos na mensagem.
Publicar um evento pequeno, que permita ao Worker localizar o registro
no banco:

``` csharp
public record SendEmailMessage(
    Guid EmailId,
    Guid CorrelationId,
    DateTime CreatedAt
);
```

## 6. Topologia RabbitMQ inicial

Topologia conceitual:

``` text
                         email.exchange
                         (direct exchange)
                                |
                     routing key: email.send
                                |
                                v
                     email.send (quorum queue)
                     x-dead-letter-exchange: email.dlx
                     x-delivery-limit: 5
                                |
                          Email Workers
                                |
        +-----------------------+------------------------+
        |                       |                        |
     sucesso             erro temporário        mensagem inválida /
  ou erro permanente    (reagendado via Outbox   limite de entregas
        |                no SQL Server)              excedido
       ACK                     ACK                       |
                                                         v
                                               email.dlx -> email.dlq
```

Filas e componentes previstos:

-   `email.exchange`: exchange principal.
-   `email.send`: fila principal de processamento (quorum queue).
-   `email.dlx`: exchange de dead-letter.
-   `email.dlq`: Dead Letter Queue para mensagens que não podem ser
    processadas e exigem análise ou reprocessamento controlado.

Não há filas de retry com atraso no RabbitMQ (decisão D2). O retry é
agendado no banco: o Worker grava um novo `OutboxMessage` com
`AvailableAt` no futuro, e o Dispatcher só o publica quando chega a
hora. Isso mantém o banco como fonte da verdade, deixa o reagendamento
na mesma transação da mudança de status e evita manter filas com TTL. A
precisão do atraso depende do intervalo de varredura do Dispatcher
(alguns segundos), o que é suficiente para backoff em minutos.

A DLQ não recebe e-mails com falha de negócio (esses ficam como `Failed`
no banco). Ela recebe:

-   mensagens que não desserializam ou apontam para e-mail inexistente
    (`BasicReject` sem requeue);
-   mensagens que excederam o `x-delivery-limit` da quorum queue, por
    exemplo quando o Worker cai repetidamente ao processá-las.

Configurações recomendadas:

-   Filas duráveis (quorum queues para `email.send` e `email.dlq`).
-   `x-delivery-limit` definido explicitamente (o RabbitMQ 4 aplica um
    padrão; não depender dele).
-   Mensagens persistentes.
-   Publisher Confirms.
-   ACK manual no consumidor.
-   Prefetch configurável.
-   Nunca usar `NACK` com requeue imediato em loop para tratar erro de
    envio.
-   Topologia declarada de forma idempotente na inicialização (API e
    Worker podem declarar sem conflito).

A topologia final deve ser validada com a versão do RabbitMQ e a
estratégia de disponibilidade escolhida.

## 7. Escalabilidade e alto volume

Várias instâncias do Worker podem consumir a mesma fila. O RabbitMQ
distribui as mensagens disponíveis entre os consumidores.

Exemplo ilustrativo:

``` text
RabbitMQ: email.send
       |
       +------ Worker 1 (concorrência 10)
       +------ Worker 2 (concorrência 10)
       +------ Worker 3 (concorrência 10)
                         |
                         v
                  Provedor de e-mail
```

Com três instâncias e concorrência dez por instância, podem existir até
30 operações de envio simultâneas. Isso não significa que o provedor
aceite essa taxa.

Controles necessários:

1.  **Rate limiting:** respeitar limites por segundo/minuto do provedor.
2.  **Concorrência limitada:** não criar tarefas ilimitadas.
3.  **Prefetch:** controlar quantas mensagens ficam sem ACK por
    consumidor.
4.  **Retry com backoff:** evitar tempestades de tentativas.
5.  **Circuit breaker:** pausar chamadas ao provedor quando houver
    falhas persistentes.
6.  **DLQ:** preservar mensagens que não podem ser processadas
    normalmente.
7.  **Observabilidade:** medir profundidade da fila, idade da mensagem
    mais antiga, throughput, falhas e latência.
8.  **Anexos (backlog):** usar object storage, como S3 ou Azure Blob,
    em vez de inserir arquivos grandes na fila ou no banco.
9.  **Alta disponibilidade:** considerar cluster RabbitMQ com três nós
    em hosts/zonas diferentes e SQL Server com backup e recuperação
    adequados.

Os valores iniciais de `PrefetchCount`, concorrência e tamanho de lote
devem ser definidos por testes de carga, não por suposição.

## 8. API planejada

| Método | Endpoint                     | Finalidade                       | Etapa |
|--------|------------------------------|----------------------------------|-------|
| POST   | `/api/v1/emails`             | Solicitar envio                  | 3     |
| GET    | `/api/v1/emails/{id}`        | Consultar status                 | 3     |
| GET    | `/api/v1/emails`             | Consultar histórico (paginado)   | 8     |
| POST   | `/api/v1/emails/{id}/retry`  | Solicitar nova tentativa manual  | 8     |
| POST   | `/api/v1/emails/{id}/cancel` | Cancelar envio pendente          | 8     |

Todas as rotas exigem o header `X-Api-Key`. Cada cliente só enxerga os
próprios e-mails: um `{id}` de outro cliente retorna `404`.

Headers do `POST /api/v1/emails`:

| Header            | Obrigatório | Descrição                                          |
|-------------------|-------------|----------------------------------------------------|
| `X-Api-Key`       | Sim         | Chave do cliente                                   |
| `Idempotency-Key` | Sim         | Identificador único da solicitação (ex.: UUID)     |
| `X-Correlation-Id`| Não         | Gerado pela API quando ausente; devolvido na resposta |

Exemplo de solicitação:

``` json
{
  "to": "cliente@email.com",
  "subject": "Bem-vindo!",
  "body": "<h1>Olá!</h1><p>Seja bem-vindo.</p>",
  "isHtml": true
}
```

A API retorna `202 Accepted`, pois o envio é assíncrono, com o header
`Location: /api/v1/emails/{id}` e o corpo:

``` json
{
  "id": "6f1c2a4e-0d3b-4f7a-9a1e-2b5c8d9e0f11",
  "status": "Pending",
  "createdAt": "2026-11-19T12:00:00Z"
}
```

Respostas de erro usam `ProblemDetails` (RFC 9457):

| Status | Situação                                                       |
|--------|----------------------------------------------------------------|
| `400`  | Payload inválido ou `Idempotency-Key` ausente                  |
| `401`  | API key ausente, inválida ou inativa                           |
| `404`  | E-mail inexistente ou de outro cliente                         |
| `409`  | Transição inválida (ex.: cancelar e-mail já enviado)           |
| `422`  | `Idempotency-Key` reutilizada com payload diferente            |

## 9. Tecnologias e bibliotecas

  Tecnologia               Uso
  ------------------------ ------------------------------------------------------------
  .NET 10 / ASP.NET Core   API REST e execução dos processos
  Entity Framework Core    Persistência SQL Server
  RabbitMQ.Client          Publicação e consumo de mensagens
  MailKit                  Envio SMTP
  FluentValidation         Validação dos comandos/DTOs
  Serilog                  Logs estruturados
  OpenTelemetry            Métricas e rastreamento distribuído
  Polly                    Políticas de resiliência em integrações, quando necessário
  xUnit                    Testes automatizados
  Testcontainers           Testes de integração com dependências em containers
  Docker Compose           Ambiente local
  Mailpit                  SMTP falso para desenvolvimento e testes

Documentação:

-   .NET 10: https://dotnet.microsoft.com/download/dotnet/10.0
-   RabbitMQ .NET Client:
    https://www.rabbitmq.com/client-libraries/dotnet-api-guide
-   RabbitMQ Quorum Queues: https://www.rabbitmq.com/docs/quorum-queues
-   RabbitMQ Confirms: https://www.rabbitmq.com/docs/confirms
-   MailKit: https://github.com/jstedfast/MailKit
-   EF Core: https://learn.microsoft.com/pt-br/ef/core/
-   Mailpit: https://mailpit.axllent.org/

## 10. Etapa 1 --- Fundação da Solution

### 10.1 Pré-requisitos

-   SDK .NET 10.
-   Docker Desktop com Docker Compose.
-   PowerShell ou terminal.
-   Editor/IDE de preferência.

Verificar versões:

``` powershell
dotnet --version
dotnet --list-sdks
docker --version
docker compose version
```

### 10.2 Criar a estrutura

``` powershell
mkdir EmailService
cd EmailService

mkdir src
mkdir tests

dotnet new sln -n EmailService --format sln
```

### 10.3 Criar os projetos

``` powershell
dotnet new webapi -n EmailService.API -o src/EmailService.API --framework net10.0 --use-controllers

dotnet new classlib -n EmailService.Application -o src/EmailService.Application --framework net10.0

dotnet new classlib -n EmailService.Domain -o src/EmailService.Domain --framework net10.0

dotnet new classlib -n EmailService.Infrastructure -o src/EmailService.Infrastructure --framework net10.0

dotnet new worker -n EmailService.Worker -o src/EmailService.Worker --framework net10.0

dotnet new xunit -n EmailService.UnitTests -o tests/EmailService.UnitTests --framework net10.0

dotnet new xunit -n EmailService.IntegrationTests -o tests/EmailService.IntegrationTests --framework net10.0
```

### 10.4 Adicionar projetos à Solution

``` powershell
dotnet sln EmailService.sln add src/EmailService.API/EmailService.API.csproj

dotnet sln EmailService.sln add src/EmailService.Application/EmailService.Application.csproj

dotnet sln EmailService.sln add src/EmailService.Domain/EmailService.Domain.csproj

dotnet sln EmailService.sln add src/EmailService.Infrastructure/EmailService.Infrastructure.csproj

dotnet sln EmailService.sln add src/EmailService.Worker/EmailService.Worker.csproj

dotnet sln EmailService.sln add tests/EmailService.UnitTests/EmailService.UnitTests.csproj

dotnet sln EmailService.sln add tests/EmailService.IntegrationTests/EmailService.IntegrationTests.csproj
```

### 10.5 Configurar referências

``` powershell
# Application -> Domain
dotnet add src/EmailService.Application reference src/EmailService.Domain

# Infrastructure -> Application e Domain
dotnet add src/EmailService.Infrastructure reference src/EmailService.Application
dotnet add src/EmailService.Infrastructure reference src/EmailService.Domain

# API -> Application e Infrastructure
dotnet add src/EmailService.API reference src/EmailService.Application
dotnet add src/EmailService.API reference src/EmailService.Infrastructure

# Worker -> Application e Infrastructure
dotnet add src/EmailService.Worker reference src/EmailService.Application
dotnet add src/EmailService.Worker reference src/EmailService.Infrastructure

# Testes
dotnet add tests/EmailService.UnitTests reference src/EmailService.Application
dotnet add tests/EmailService.UnitTests reference src/EmailService.Domain

dotnet add tests/EmailService.IntegrationTests reference src/EmailService.Infrastructure
dotnet add tests/EmailService.IntegrationTests reference src/EmailService.API
dotnet add tests/EmailService.IntegrationTests reference src/EmailService.Worker
```

A referência para API permite testes com `WebApplicationFactory` (Etapa
3), e a referência para Worker permite testar consumidores e hosted
services (Etapas 4 e 5).

### 10.6 Criar docker-compose.yml

Na raiz do repositório:

``` yaml
services:
  sqlserver:
    image: mcr.microsoft.com/mssql/server:2022-latest
    container_name: email-sqlserver
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_PID: Developer
      MSSQL_SA_PASSWORD: ${MSSQL_SA_PASSWORD}
      MSSQL_MEMORY_LIMIT_MB: "2048"
    ports:
      - "1433:1433"
    volumes:
      - sqlserver_data:/var/opt/mssql
    healthcheck:
      test: ["CMD-SHELL", "/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P \"$$MSSQL_SA_PASSWORD\" -Q \"SELECT 1\" -b -o /dev/null"]
      interval: 10s
      timeout: 5s
      retries: 10
      start_period: 30s
    restart: unless-stopped

  rabbitmq:
    image: rabbitmq:4-management
    container_name: email-rabbitmq
    environment:
      RABBITMQ_DEFAULT_USER: ${RABBITMQ_USER}
      RABBITMQ_DEFAULT_PASS: ${RABBITMQ_PASSWORD}
    ports:
      - "5672:5672"
      - "15672:15672"
    volumes:
      - rabbitmq_data:/var/lib/rabbitmq
    healthcheck:
      test: ["CMD", "gosu", "rabbitmq", "rabbitmq-diagnostics", "-q", "ping"]
      interval: 10s
      timeout: 5s
      retries: 10
      start_period: 20s
    restart: unless-stopped

  mailpit:
    image: axllent/mailpit:latest
    container_name: email-mailpit
    ports:
      - "1025:1025"   # SMTP
      - "8025:8025"   # Interface web
    restart: unless-stopped

volumes:
  sqlserver_data:
  rabbitmq_data:
```

Observações:

-   No healthcheck do SQL Server, `$$` faz o Compose passar
    `$MSSQL_SA_PASSWORD` literalmente, e o shell do container expande a
    variável.
-   A senha do SQL Server precisa atender à política de complexidade
    (mínimo de 8 caracteres, com maiúsculas, minúsculas, números e
    símbolos), ou o container não inicia.
-   O Mailpit aceita qualquer e-mail sem entregá-lo de verdade, e a
    interface web mostra as mensagens recebidas. A imagem já traz um
    healthcheck próprio.
-   `MSSQL_MEMORY_LIMIT_MB` limita o SQL Server a 2 GB. Sem isso ele
    tenta usar ~80% da memória da VM do Docker; em hosts com pouca
    memória de commit (por exemplo, Windows sem arquivo de paginação) a
    VM do WSL cai e os volumes ficam corrompidos no primeiro boot.
-   O healthcheck do RabbitMQ roda como o usuário `rabbitmq` (`gosu`).
    Rodando como root, ele pode criar o `.erlang.cookie` antes do
    servidor, que então falha com `eacces` ao lê-lo.
-   Depois que o ambiente estiver estável, fixar as versões das imagens
    (evitar `latest`).

### 10.7 Criar .env

Arquivo local `.env`:

``` env
MSSQL_SA_PASSWORD=Use_Uma_Senha_Local_Forte_123!
RABBITMQ_USER=emailservice
RABBITMQ_PASSWORD=Use_Outra_Senha_Local_Forte_123!
```

Essas credenciais são apenas exemplos locais. Não usar em produção.

Criar `.env.example` para documentar as variáveis sem compartilhar
segredos:

``` env
MSSQL_SA_PASSWORD=ChangeThis_LocalPassword_123!
RABBITMQ_USER=emailservice
RABBITMQ_PASSWORD=ChangeThis_RabbitPassword_123!
```

### 10.8 Criar .gitignore

``` gitignore
.env
**/bin/
**/obj/
.vs/
.vscode/
*.user
*.suo
```

### 10.9 Subir os serviços

``` powershell
docker compose up -d
docker compose ps
docker compose logs -f
```

Acessos locais:

  Serviço               Endereço/porta
  --------------------- ------------------------
  SQL Server            `localhost,1433`
  SQL Server usuário    `sa`
  RabbitMQ AMQP         `localhost:5672`
  RabbitMQ Management   http://localhost:15672
  RabbitMQ usuário      `emailservice`
  Mailpit SMTP          `localhost:1025`
  Mailpit interface     http://localhost:8025

As senhas devem ser lidas do arquivo `.env`.

Confirmar que todos os containers estão `healthy`:

``` powershell
docker compose ps
```

### 10.10 Configurar DI básica

Cada camada expõe um método de extensão que registra as próprias
dependências. API e Worker são os pontos de composição e só chamam esses
métodos.

Pacotes:

``` powershell
dotnet add src/EmailService.Application package Microsoft.Extensions.DependencyInjection.Abstractions

dotnet add src/EmailService.Infrastructure package Microsoft.Extensions.DependencyInjection.Abstractions
dotnet add src/EmailService.Infrastructure package Microsoft.Extensions.Configuration.Abstractions
```

`src/EmailService.Application/DependencyInjection.cs`:

``` csharp
using Microsoft.Extensions.DependencyInjection;

namespace EmailService.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Casos de uso e validadores serão registrados aqui (Etapa 3).
        return services;
    }
}
```

`src/EmailService.Infrastructure/DependencyInjection.cs`:

``` csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EmailService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // DbContext, repositórios, RabbitMQ e SMTP serão registrados aqui
        // (Etapas 2, 4 e 6).
        return services;
    }
}
```

No `Program.cs` da API e do Worker:

``` csharp
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);
```

### 10.11 Validar a Solution

``` powershell
dotnet sln EmailService.sln list
dotnet build
dotnet test
```

Critérios de conclusão:

-   [ ] SDK .NET 10 instalado.
-   [ ] Solution criada.
-   [ ] Cinco projetos principais criados.
-   [ ] Projetos de testes criados.
-   [ ] Referências entre projetos configuradas.
-   [ ] `AddApplication` e `AddInfrastructure` chamados pela API e pelo
    Worker.
-   [ ] SQL Server, RabbitMQ e Mailpit executando no Docker, com status
    `healthy`.
-   [ ] Painel RabbitMQ acessível.
-   [ ] Interface do Mailpit acessível.
-   [ ] `.env` fora do controle de versão e `.env.example` criado.
-   [ ] `dotnet build` sem erros.
-   [ ] `dotnet test` sem erros.

## 11. Plano de implementação completo

Cada etapa termina com uma **entrega demonstrável** e com os próprios
testes. Uma etapa só é considerada concluída quando a entrega pode ser
reproduzida e os testes passam.

Visão geral:

| #  | Etapa                    | Entrega                                                        |
|----|--------------------------|----------------------------------------------------------------|
| 1  | Fundação da Solution     | Solution compila, testes passam, containers `healthy`          |
| 2  | Domain e persistência    | E-mail e Outbox gravados atomicamente, provado por teste       |
| 3  | API de entrada           | `POST` autenticado e idempotente gera `Pending` + Outbox       |
| 4  | Mensageria               | Eventos da Outbox chegam à fila sem perda, mesmo com broker fora |
| 5  | Worker com sender fake   | Fluxo de ponta a ponta até `Sent` com envio simulado           |
| 6  | Envio SMTP real          | E-mail visível no Mailpit, erros SMTP classificados — **MVP**  |
| 7  | Resiliência              | Retry, DLQ e recuperação comprovados por testes de falha       |
| 8  | Operação                 | Histórico, retry manual, cancelamento e reprocessamento da DLQ |
| 9  | Observabilidade          | Métricas, traces e health checks de API e Worker               |
| 10 | Produção e escala        | Imagens Docker, testes de carga e parâmetros calibrados        |
| —  | Backlog                  | Anexos, webhooks do provedor, templates                        |

### Etapa 1 --- Fundação da Solution

Itens:

-   Criar Solution e projetos (seção 10).
-   Configurar referências entre projetos, incluindo API e Worker nos
    testes de integração.
-   Configurar DI básica (`AddApplication`, `AddInfrastructure`).
-   Subir SQL Server, RabbitMQ e Mailpit com Docker Compose e
    healthchecks.
-   Criar `.env`, `.env.example` e `.gitignore`.

Entrega: `dotnet build` e `dotnet test` sem erros e `docker compose ps`
com todos os serviços `healthy`.

### Etapa 2 --- Domain e persistência

Itens:

-   Criar `Email`, `EmailAttempt`, `OutboxMessage`, `IdempotencyKey` e
    `ApiClient`, com todos os campos da seção 5.
-   Definir `EmailStatus`, `AttemptOutcome` e as transições como métodos
    da entidade `Email`, com exceções de domínio.
-   Configurar `AppDbContext` e Fluent API, incluindo `RowVersion` e os
    índices da seção 5.
-   Criar migrations.
-   Implementar repositórios e unidade de trabalho (transação SQL).
-   Criar seed de um `ApiClient` para desenvolvimento.

Testes:

-   Unitários: todas as transições válidas e inválidas de `Email`.
-   Integração (Testcontainers + SQL Server): gravação atômica de
    `Email` + `OutboxMessage` + `IdempotencyKey`, incluindo rollback
    quando uma das gravações falha.
-   Integração: atualização concorrente bloqueada por `RowVersion`.

Entrega: registrar um e-mail e seu evento Outbox na mesma transação,
ainda sem API, sem RabbitMQ e sem envio.

Decisões tomadas na implementação:

-   As transições de `Email` recebem o horário (`now`) como parâmetro,
    e o Domain não depende de relógio. Na Application, o relógio é um
    `TimeProvider` injetado.
-   `Attempts` conta tentativas **concluídas**: é incrementado por
    `MarkSent`, `ScheduleRetry` e `MarkFailed`. `ScheduleRetry` recusa a
    transição quando não restam tentativas (`HasAttemptsRemaining`), e
    quem chama deve usar `MarkFailed`.
-   O retry manual (`RetryManually`, `Failed -> Pending`) zera
    `Attempts`; sem isso, o e-mail falharia de novo na primeira
    tentativa.
-   A unidade de trabalho é o `SaveChanges` do EF Core, que grava tudo
    em uma única transação. Conflitos de `RowVersion` viram
    `ConcurrencyConflictException` (camada Application).
-   Todas as datas são UTC. O `AppDbContext` restaura o `DateTimeKind`
    na leitura.
-   O seed do `ApiClient` de desenvolvimento roda na inicialização da
    API, apenas em Development, com a chave lida de
    `DevelopmentSeed:ApiKey`. Não é feito por migration: o repositório
    é público, e uma chave conhecida acabaria em qualquer banco migrado.
-   As PKs são `Guid` aleatórios (`Guid.NewGuid`). Revisar na Etapa 10:
    índices clusterizados em GUID aleatório fragmentam com alto volume.

Como rodar localmente (uma vez por máquina):

``` powershell
# Connection string e chave de dev ficam em User Secrets, fora do repositório
dotnet user-secrets set "ConnectionStrings:EmailServiceDb" "Server=localhost,1433;Database=EmailService;User Id=sa;Password=<senha do .env>;TrustServerCertificate=True" --project src/EmailService.API
dotnet user-secrets set "ConnectionStrings:EmailServiceDb" "<mesma connection string>" --project src/EmailService.Worker
dotnet user-secrets set "DevelopmentSeed:ApiKey" "es_dev_<pelo menos 32 caracteres aleatórios>" --project src/EmailService.API

# Aplicar as migrations
$env:ConnectionStrings__EmailServiceDb = "<mesma connection string>"
dotnet ef database update --project src/EmailService.Infrastructure
```

Os testes de integração sobem um SQL Server próprio via Testcontainers,
com o mesmo limite de 2 GB do compose. Em máquinas com pouca memória,
pare o SQL Server do compose antes (`docker compose stop sqlserver`).

### Etapa 3 --- API de entrada

Itens:

-   Autenticação por API key (`X-Api-Key`), validando o hash em
    `ApiClients` e identificando o `ClientId`.
-   `POST /api/v1/emails` com DTOs e validação (FluentValidation).
-   Caso de uso `SendEmail`: persistir `Email`, `OutboxMessage` e
    `IdempotencyKey` atomicamente.
-   Idempotência pelo header `Idempotency-Key`: mesma chave e mesmo
    payload devolvem a resposta original; payload diferente devolve
    `422`.
-   Retornar `202 Accepted` com `Location` e o corpo da seção 8.
-   `GET /api/v1/emails/{id}` restrito ao cliente dono do e-mail.
-   Erros padronizados com `ProblemDetails`.
-   Serilog com logs estruturados e `CorrelationId` (header
    `X-Correlation-Id`, gerado quando ausente).

Testes:

-   Unitários: validadores e caso de uso `SendEmail`.
-   Integração (`WebApplicationFactory` + Testcontainers): `401` sem
    chave, `400` com payload inválido, `202` com chave válida, repetição
    idempotente, `422` com payload diferente, `404` para e-mail de outro
    cliente.

Entrega: via HTTP, um cliente autenticado cria um e-mail `Pending` com
evento na Outbox; reenviar a mesma solicitação não cria duplicata.

### Etapa 4 --- Mensageria

Itens:

-   Configurar conexão RabbitMQ (com reconexão automática).
-   Declarar a topologia da seção 6 (`email.exchange`, `email.send`
    como quorum queue com `x-delivery-limit`, `email.dlx`, `email.dlq`).
-   Implementar o Outbox Dispatcher como `HostedService` no Worker:
    reivindicação de lote por *lease*, publicação persistente com
    Publisher Confirms, marcação de `PublishedAt` após confirmação.
-   Tratar falhas de publicação: incrementar `PublishAttempts`, gravar
    `LastError`, liberar o lease para nova tentativa.
-   Propagar `CorrelationId` nos headers da mensagem.

Testes:

-   Integração (Testcontainers + RabbitMQ + SQL Server): evento
    pendente é publicado e marcado; evento com `AvailableAt` futuro não
    é publicado antes da hora.
-   Duas instâncias do Dispatcher não publicam o mesmo evento.
-   Com o RabbitMQ parado, eventos permanecem pendentes e são
    publicados quando o broker volta.

Entrega: um `POST` na API resulta em mensagem na fila `email.send`
(visível no painel do RabbitMQ), sem perda mesmo com o broker
indisponível temporariamente.

### Etapa 5 --- Worker com sender fake

Itens:

-   Definir `IEmailSender` e o tipo de resultado classificado
    (sucesso, temporário, permanente, ambíguo).
-   Implementar `FakeEmailSender` configurável (sucesso ou falha
    simulada).
-   Criar consumidor RabbitMQ com ACK manual e prefetch configurável.
-   Controle de concorrência limitado por configuração.
-   Carregar o e-mail, descartar com ACK se não estiver `Pending`.
-   Transição condicional `Pending -> Processing` com `LockedUntil`.
-   Registrar tentativas em `EmailAttempts` e atualizar o status.
-   Mensagem inválida: `BasicReject` sem requeue (vai para a DLQ).
-   Nesta etapa ainda não há retry: qualquer erro marca `Failed`.
-   Serilog no Worker com `CorrelationId` vindo da mensagem.

Testes:

-   Unitários: lógica de processamento com repositórios e sender
    simulados.
-   Integração: mensagem duplicada não gera envio duplicado; mensagem
    inválida vai para a DLQ.

Entrega: fluxo de ponta a ponta `POST` → Outbox → RabbitMQ → Worker →
`Sent`, usando o sender fake.

### Etapa 6 --- Envio SMTP real (MVP)

Itens:

-   Implementar `SmtpEmailSender` com MailKit.
-   Configurar host, porta, TLS e credenciais via configuração segura
    (User Secrets em desenvolvimento, variáveis de ambiente fora dele).
-   Implementar a classificação de erros SMTP da seção 3.
-   Gravar `ProviderMessageId`.
-   Reutilizar conexões SMTP entre envios, quando o provedor permitir.

Testes:

-   Integração com Mailpit (Testcontainers): e-mail enviado e
    recebido, conteúdo HTML e texto corretos.
-   Unitários: mapeamento de exceções e códigos SMTP para a
    classificação.

Entrega: e-mail solicitado pela API aparece no Mailpit, com status
`Sent` e `ProviderMessageId` gravados. **Este é o MVP funcional.**

### Etapa 7 --- Resiliência

Itens:

-   Retry com backoff exponencial e jitter, reagendado via Outbox
    (`AvailableAt`), respeitando `MaxAttempts`.
-   Job de recuperação de `Processing` com `LockedUntil` expirado.
-   Rate limiting do envio conforme os limites do provedor
    (`System.Threading.RateLimiting`).
-   Circuit breaker nas chamadas ao provedor (Polly): com o circuito
    aberto, reagendar sem consumir tentativas.
-   Job de limpeza (retenção) da seção 5.

Testes:

-   Erro temporário reagenda com atraso crescente; erro permanente
    marca `Failed` sem retry; tentativas esgotadas marcam `Failed`.
-   Matar o Worker no meio do envio: o e-mail é recuperado e
    reprocessado.
-   SMTP indisponível: circuito abre, fila acumula, nada se perde, e o
    processamento retoma quando o SMTP volta.

Entrega: cenários de falha acima demonstrados por testes automatizados.

### Etapa 8 --- Operação

Itens:

-   `GET /api/v1/emails` com paginação por cursor e filtros (status,
    período).
-   `POST /api/v1/emails/{id}/retry` para e-mails `Failed`.
-   `POST /api/v1/emails/{id}/cancel` para e-mails `Pending`.
-   Ferramenta de reprocessamento controlado da DLQ (comando ou endpoint
    administrativo).

Testes:

-   Integração dos três endpoints, incluindo `409` em transições
    inválidas e isolamento entre clientes.
-   Reprocessamento da DLQ devolve mensagens válidas à fila principal.

Entrega: operador consegue consultar, cancelar, reenviar e reprocessar
sem acesso direto ao banco.

### Etapa 9 --- Observabilidade

Itens:

-   OpenTelemetry: traces da API até o envio (propagando contexto pela
    mensagem) e métricas.
-   Métricas de negócio: enviados, falhas por classificação, duração do
    envio, tentativas por e-mail.
-   Métricas de fila: profundidade, idade da mensagem mais antiga,
    eventos pendentes na Outbox.
-   Health checks (`/health/live` e `/health/ready`) na API e no Worker,
    verificando SQL Server, RabbitMQ e SMTP.

Entrega: painel local (ex.: Grafana ou Aspire Dashboard) mostrando
throughput, latência, falhas e backlog durante um envio em lote.

### Etapa 10 --- Produção e escala

Itens:

-   Rate limiting por cliente na API.
-   Dockerfiles para API e Worker e compose com várias instâncias do
    Worker.
-   Testes de carga e calibração de prefetch, concorrência e tamanho de
    lote do Dispatcher.
-   Revisão de índices com planos de execução reais e avaliação de
    particionamento de `Emails`.
-   Definir alta disponibilidade (cluster RabbitMQ com três nós, SQL
    Server com backup e recuperação) e alertas.

Entrega: relatório de carga com throughput, latência e backlog nos
picos estimados, e os parâmetros calibrados documentados.

### Backlog (fora do MVP)

-   Anexos em object storage (S3 ou Azure Blob) e tabela
    `EmailAttachments`.
-   Webhooks/eventos do provedor para entrega, rejeição e bounce.
-   Templates de e-mail.
-   Múltiplos provedores com failover.

## 12. Próxima atividade

Etapas 1 e 2 concluídas. Próxima: Etapa 3, API de entrada.

A próxima entrega funcional será um `POST` autenticado e idempotente que
cria um e-mail `Pending` com seu evento na Outbox. Depois vêm o
Dispatcher (Etapa 4) e o Worker (Etapa 5).

------------------------------------------------------------------------

**Nota:** esta é a arquitetura inicial de desenvolvimento. Parâmetros de
concorrência, prefetch, topologia final, política de retry e capacidade
de produção precisam ser validados por testes de carga e pelos limites
reais do provedor SMTP.
