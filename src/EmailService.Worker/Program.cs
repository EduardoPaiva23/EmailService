using EmailService.Application;
using EmailService.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

// Hosted services (Outbox Dispatcher, consumidor RabbitMQ, jobs de
// recuperação e limpeza) serão registrados aqui a partir da Etapa 4.

var host = builder.Build();
host.Run();
