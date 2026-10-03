using System.Globalization;
using System.Text.Json;
using NexusStackNext.Auditing.Application;
using NexusStackNext.Auditing.Infrastructure;
using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Auditing.Endpoints;

/// <summary>独立来源维护入口，不启动业务宿主。</summary>
public static class OperationJournalCommand
{
    /// <summary>处理维护参数并返回安全退出码。</summary>
    /// <param name="arguments">不含 operation-journal 前缀的参数。</param>
    /// <returns>进程退出码。</returns>
    public static async Task<int> RunAsync(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var command = Parse(arguments);
        if (command is null)
        {
            Console.Error.WriteLine("operation_journal.command_invalid");
            return 2;
        }
        try
        {
            using var configuration = new ConfigurationManager();
            configuration.AddEnvironmentVariables();
            var connection = configuration.GetConnectionString("OperationJournal");
            if (string.IsNullOrWhiteSpace(connection)) { throw new InvalidOperationException(); }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var services = new ServiceCollection();
            var capacity = configuration.GetSection("OperationJournal:Capacity").Get<OperationJournalCapacityOptions>() ?? new();
            var cleanup = configuration.GetSection("OperationJournal:Cleanup").Get<OperationJournalCleanupOptions>() ?? new();
            services.AddOperationJournalPostgresStorage(connection, capacity, cleanup);
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var maintenance = scope.ServiceProvider.GetRequiredService<IOperationJournalMaintenance>();
            return command.Kind switch
            {
                "show" => Print(await maintenance.GetDeliveryAsync(command.Identity, timeout.Token).ConfigureAwait(false)),
                "receipt" => Print(await maintenance.GetRecoveryAsync(command.Identity, timeout.Token).ConfigureAwait(false)),
                _ => Print(await maintenance.RetryDeliveryAsync(command.Recovery!,
                    new OperationJournalRecoveryActor(Environment.UserName, Environment.MachineName), timeout.Token).ConfigureAwait(false)),
            };
        }
        catch (Exception)
        {
            Console.Error.WriteLine("operation_journal.command_failed");
            return 1;
        }
    }

    private static int Print<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            Console.Error.WriteLine(result.Error.Code);
            return 3;
        }
        Console.WriteLine(JsonSerializer.Serialize(result.Value, JsonSerializerOptions.Web));
        return 0;
    }

    private static Command? Parse(string[] arguments)
    {
        if (arguments is ["show" or "receipt", var identity] && Guid.TryParse(identity, out var id) && id != Guid.Empty)
        {
            return new(arguments[0], id, null);
        }
        if (arguments is ["retry", var requestIdentity, var messageIdentity, var stopped, var revision, "dependency-restored" or "manual-retry"]
            && Guid.TryParse(requestIdentity, out var requestId) && requestId != Guid.Empty
            && Guid.TryParse(messageIdentity, out var messageId) && messageId != Guid.Empty
            && DateTimeOffset.TryParseExact(stopped, ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stoppedAt)
            && long.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out var retryRevision))
        {
            return new("retry", requestId, new(requestId, messageId, stoppedAt, retryRevision, arguments[5]));
        }
        return null;
    }

    private sealed record Command(string Kind, Guid Identity, OperationJournalRecoveryRequest? Recovery);
}
