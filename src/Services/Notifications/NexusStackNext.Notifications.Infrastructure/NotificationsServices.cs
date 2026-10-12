using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Notifications.Application;

namespace NexusStackNext.Notifications.Infrastructure;

/// <summary>Notifications 的显式存储及用例注册。</summary>
public static class NotificationsServices
{
    /// <summary>本上下文事实的独立 Outbox。</summary>
    public const string OutboxKey = "notifications";

    /// <summary>注册持久邮件用例。</summary>
    public static IServiceCollection AddNotificationsPostgres(this IServiceCollection services, string connectionString, SmtpOptions smtp)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(smtp);
        smtp.Validate();
        services.AddSingleton(smtp);
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IIntegrationEventSerializer, SystemTextJsonIntegrationEventSerializer>();
        services.AddDbContext<NotificationsDbContext>((provider, options) => options
            .UseNexusStackPostgres(connectionString, NotificationsDbContext.SchemaName)
            .UseNexusStackAuditInterceptor(provider));
        services.AddHostedService<NotificationsStartupCheck>();
        services.AddHealthChecks().AddCheck<NotificationsHealthCheck>("notifications-database");
        services.AddScoped<ICommandHandler<SubmitEmail, EmailStatus>, EmailCommands>();
        services.AddScoped<IQueryHandler<GetEmail, EmailStatus>, EmailCommands>();
        services.AddScoped<ICommandHandler<ResolveEmail, EmailStatus>, EmailCommands>();
        services.AddScoped<ICommandHandler<DispatchPendingEmail, int>, EmailDeliveryRunner>();
        services.AddScoped<EmailFacts>();
        services.AddKeyedScoped<IOutboxStore, EfOutboxStore<NotificationsDbContext>>(OutboxKey);
        return services;
    }
}
