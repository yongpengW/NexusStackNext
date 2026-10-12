using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Events;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Notifications.Application;
using NexusStackNext.Notifications.Infrastructure;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class SmtpEmailJourneyTests(JourneyDatabaseTemplates databases)
{
    private static async Task<int> DispatchAsync(IServiceProvider services, CancellationToken token = default) =>
        (await services.GetRequiredService<ISender>().SendAsync(new DispatchPendingEmail(), token)).Value;

    [PostgresFact]
    public async Task Rejected_fact_rolls_back_mail_and_same_request_can_be_submitted_after_recovery()
    {
        await using var database = await databases.CreateAsync();
        await using (await JourneyDatabaseOperation.EnterAsync(preparation: true)) { await NotificationsDatabase.MigrateAsync(database.ConnectionString); }
        await using var provider = BuildProvider(database.ConnectionString, 587);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var inject = new NpgsqlCommand("ALTER TABLE notifications.outbox ADD CONSTRAINT reject_test_fact CHECK (\"EventName\" <> 'notifications.email-delivery-committed.v1')", connection))
        { await inject.ExecuteNonQueryAsync(); }
        var request = new SubmitEmail(Guid.NewGuid(), ["recipient@example.test"], "test", "report", null, []);
        try
        {
            await using var scope = provider.CreateAsyncScope();
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(request));
        }
        finally
        {
            await using var recover = new NpgsqlCommand("ALTER TABLE notifications.outbox DROP CONSTRAINT reject_test_fact", connection);
            await recover.ExecuteNonQueryAsync();
        }
        await using var retry = provider.CreateAsyncScope();
        var sender = retry.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal("notifications.not_found", (await sender.QueryAsync(new GetEmail(request.RequestId))).Error.Code);
        Assert.True((await sender.SendAsync(request)).IsSuccess);
        Assert.Single(await retry.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(NotificationsServices.OutboxKey).ReadPendingAsync(10, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Smtp_authentication_is_explicit_and_rejection_never_sends_content(bool acceptAuthentication)
    {
        await using var receiver = new TestSmtpReceiver { AdvertiseAuthentication = true, AcceptAuthentication = acceptAuthentication };
        await using var database = await databases.CreateAsync();
        await using (await JourneyDatabaseOperation.EnterAsync(preparation: true)) { await NotificationsDatabase.MigrateAsync(database.ConnectionString); }
        await using var provider = BuildProvider(database.ConnectionString, receiver.Port, authenticate: true);
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var id = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new SubmitEmail(id, ["recipient@example.test"], "test", "report", null, []))).IsSuccess);
        Assert.Equal(1, await DispatchAsync(scope.ServiceProvider));
        Assert.Equal(1, receiver.AuthenticationAttempts);
        Assert.Equal(acceptAuthentication ? "Accepted" : "Failed", (await sender.QueryAsync(new GetEmail(id))).Value.State);
        Assert.Equal(acceptAuthentication ? 1 : 0, receiver.Messages.Count);
    }

    [PostgresFact]
    public async Task Active_smtp_does_not_hold_database_transaction_and_cancellation_never_requeues_it()
    {
        await using var receiver = new TestSmtpReceiver { Outcome = "hold" };
        await using var database = await databases.CreateAsync();
        await using (await JourneyDatabaseOperation.EnterAsync(preparation: true)) { await NotificationsDatabase.MigrateAsync(database.ConnectionString); }
        await using var provider = BuildProvider(database.ConnectionString, receiver.Port);
        var id = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        { Assert.True((await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new SubmitEmail(id, ["recipient@example.test"], "test", "report", null, []))).IsSuccess); }
        await using var first = provider.CreateAsyncScope();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var active = DispatchAsync(first.ServiceProvider, cancel.Token);
        await receiver.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var second = provider.CreateAsyncScope())
        {
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Assert.Equal(0, await DispatchAsync(second.ServiceProvider, bounded.Token));
            Assert.False((await second.ServiceProvider.GetRequiredService<ISender>().SendAsync(new ResolveEmail(id, 1, "resend"), bounded.Token)).IsSuccess);
        }
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await using var recovered = provider.CreateAsyncScope();
        Assert.Equal("Unknown", (await recovered.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetEmail(id))).Value.State);
        Assert.Equal(0, await DispatchAsync(recovered.ServiceProvider));
        Assert.Single(receiver.Messages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Required_tls_and_recipient_rejection_fail_before_sending_content(bool requireTls)
    {
        await using var receiver = new TestSmtpReceiver { Outcome = "recipient-reject" };
        await using var database = await databases.CreateAsync();
        await using (await JourneyDatabaseOperation.EnterAsync(preparation: true)) { await NotificationsDatabase.MigrateAsync(database.ConnectionString); }
        await using var provider = BuildProvider(database.ConnectionString, receiver.Port,
            security: requireTls ? MailKit.Security.SecureSocketOptions.StartTls : MailKit.Security.SecureSocketOptions.None);
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var id = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new SubmitEmail(id, ["first@example.test", "second@example.test"], "test", "report", null, []))).IsSuccess);
        Assert.Equal(1, await DispatchAsync(scope.ServiceProvider));
        Assert.Equal("Failed", (await sender.QueryAsync(new GetEmail(id))).Value.State);
        Assert.Empty(receiver.Messages);
    }

    private static ServiceProvider BuildProvider(string connection, int port, MailKit.Security.SecureSocketOptions security = MailKit.Security.SecureSocketOptions.None,
        string owner = "mail-owner", bool authenticate = false, IClock? clock = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexusStackApplication();
        if (clock is not null) { services.AddSingleton(clock); }
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser(owner));
        services.AddNotificationsPostgres(connection, new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Sender = "sender@example.test",
            Security = security,
            UserName = authenticate ? "test-user" : null,
            Password = authenticate ? "local-test-only" : null,
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Theory]
    [InlineData("drop", "Unknown", "confirm")]
    [InlineData("drop", "Unknown", "resend")]
    [InlineData("reject", "Failed", "retry")]
    public async Task Uncertain_and_rejected_delivery_require_conditional_disposition(string outcome, string expected, string decision)
    {
        await using var receiver = new TestSmtpReceiver { Outcome = outcome };
        await using var database = await databases.CreateAsync();
        await using (await JourneyDatabaseOperation.EnterAsync(preparation: true)) { await NotificationsDatabase.MigrateAsync(database.ConnectionString); }
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        await using var provider = BuildProvider(database.ConnectionString, receiver.Port, clock: clock);
        var id = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.True((await sender.SendAsync(new SubmitEmail(id, ["recipient@example.test"], "test", "private report", null, []))).IsSuccess);
            Assert.Equal(1, await DispatchAsync(scope.ServiceProvider));
            var status = (await sender.QueryAsync(new GetEmail(id))).Value;
            Assert.Equal(expected, status.State);
            Assert.Equal(1, status.Attempt);
            Assert.Equal(expected == "Unknown" ? 2 : 3, status.Version);
            Assert.Equal(0, await DispatchAsync(scope.ServiceProvider));
            Assert.Single(receiver.Messages);
            Assert.False((await sender.SendAsync(new ResolveEmail(id, 0, decision))).IsSuccess);
            if (expected == "Unknown") { Assert.False((await sender.SendAsync(new ResolveEmail(id, 1, decision))).IsSuccess); }
        }
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        // 新作用域重读持久状态；真实进程重启另由 HTTP 旅程验证。
        await using (var scope = provider.CreateAsyncScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            Assert.Equal(expected, (await sender.QueryAsync(new GetEmail(id))).Value.State);
            var resolved = await sender.SendAsync(new ResolveEmail(id, 1, decision));
            Assert.True(resolved.IsSuccess, resolved.Error.Code);
            Assert.False((await sender.SendAsync(new ResolveEmail(id, 1, decision))).IsSuccess);
            receiver.Outcome = "accept";
            Assert.Equal(decision == "confirm" ? 0 : 1, await DispatchAsync(scope.ServiceProvider));
            Assert.Equal("Accepted", (await sender.QueryAsync(new GetEmail(id))).Value.State);
            Assert.Equal(decision == "confirm" ? 1 : 2, receiver.Messages.Count);
            var ids = receiver.Messages.Select(raw => MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw))).MessageId).Distinct().ToArray();
            Assert.Single(ids);
        }
    }

    [PostgresFact]
    public async Task Smtp_acceptance_preserves_multipart_report_and_does_not_send_it_again()
    {
        await using var receiver = new TestSmtpReceiver();
        await using var database = await databases.CreateAsync();
        await using (await JourneyDatabaseOperation.EnterAsync(preparation: true))
        {
            await NotificationsDatabase.MigrateAsync(database.ConnectionString);
        }
        await using var provider = BuildProvider(database.ConnectionString, receiver.Port);
        var id = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new SubmitEmail(id,
                ["first@example.test", "second@example.test"], "成本报告", "report", "<p>成本报告</p>", [new("report.bin", "application/octet-stream", [0, 1, 255])]));
            Assert.True(result.IsSuccess, result.Error.Code);
        }
        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.Equal(1, await DispatchAsync(scope.ServiceProvider));
        }
        await using (var scope = provider.CreateAsyncScope())
        {
            var status = await scope.ServiceProvider.GetRequiredService<ISender>().QueryAsync(new GetEmail(id));
            Assert.Equal("Accepted", status.Value.State);
            Assert.Equal(1, status.Value.Attempt);
            Assert.Equal(3, status.Value.Version);
            Assert.NotNull(status.Value.UpdatedAt);
            Assert.Equal(0, await DispatchAsync(scope.ServiceProvider));
        }
        var raw = Assert.Single(receiver.Messages);
        using var message = await MimeMessage.LoadAsync(new MemoryStream(Encoding.UTF8.GetBytes(raw)));
        Assert.Equal(id.ToString("N") + "@notifications.nexusstack.invalid", message.MessageId);
        Assert.Equal(2, message.To.Count);
        Assert.Equal("report", message.TextBody);
        Assert.Equal("<p>成本报告</p>", message.HtmlBody);
        var attachment = Assert.IsType<MimePart>(Assert.Single(message.Attachments));
        Assert.NotNull(attachment.Content);
        using var content = new MemoryStream();
        await attachment.Content.DecodeToAsync(content);
        Assert.Equal(new byte[] { 0, 1, 255 }, content.ToArray());
    }

    [PostgresFact]
    public async Task Stable_request_is_accepted_once_and_changed_content_conflicts_without_sending()
    {
        await using var database = await databases.CreateAsync();
        await using (await JourneyDatabaseOperation.EnterAsync(preparation: true))
        {
            await NotificationsDatabase.MigrateAsync(database.ConnectionString);
        }
        await using var provider = BuildProvider(database.ConnectionString, 587);
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new SubmitEmail(Guid.NewGuid(), ["recipient@example.test"], "演示报告", "plain report", "<p>report</p>", []);

        await using var concurrent = provider.CreateAsyncScope();
        var results = await Task.WhenAll(sender.SendAsync(request), concurrent.ServiceProvider.GetRequiredService<ISender>().SendAsync(request));
        var first = results[0];
        Assert.True(results[1].IsSuccess, results[1].Error.Code);
        Assert.Equal(first.Value, results[1].Value);
        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal("Pending", first.Value.State);
        Assert.Equal(0, first.Value.Attempt);
        Assert.Equal(1, first.Value.Version);
        var replay = await sender.SendAsync(request);
        Assert.Equal(first.Value, replay.Value);
        var conflict = await sender.SendAsync(request with { Subject = "different" });
        Assert.False(conflict.IsSuccess);
        Assert.Equal("notifications.conflict", conflict.Error.Code);
        var facts = await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>(NotificationsServices.OutboxKey).ReadPendingAsync(20, DateTimeOffset.UtcNow);
        Assert.Single(facts);
        Assert.DoesNotContain("recipient@example.test", facts[0].Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("plain report", facts[0].Payload, StringComparison.Ordinal);
        await using var other = BuildProvider(database.ConnectionString, 587, owner: "other-owner");
        await using var otherScope = other.CreateAsyncScope();
        var otherSender = otherScope.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal("notifications.not_found", (await otherSender.QueryAsync(new GetEmail(request.RequestId))).Error.Code);
        Assert.Equal("notifications.conflict", (await otherSender.SendAsync(request)).Error.Code);
        Assert.Equal("notifications.not_found", (await otherSender.SendAsync(new ResolveEmail(request.RequestId, 0, "confirm"))).Error.Code);
    }
}

// 真正的本地 SMTP 对端，断言可观察的协议内容，不替换发送实现。
internal sealed class TestSmtpReceiver : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task accept;
    private readonly List<Task> connections = [];
    public TestSmtpReceiver()
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accept = AcceptAsync();
    }
    public int Port { get; }
    public List<string> Messages { get; } = [];
    public string Outcome { get; set; } = "accept";
    public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool AdvertiseAuthentication { get; init; }
    public bool AcceptAuthentication { get; init; } = true;
    public int AuthenticationAttempts { get; private set; }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                connections.Add(ReceiveAsync(client));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private async Task ReceiveAsync(TcpClient client)
    {
        using (client)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync("220 localhost test SMTP");
            while (await reader.ReadLineAsync(stop.Token) is { } line)
            {
                if (line.StartsWith("EHLO", StringComparison.Ordinal) || line.StartsWith("HELO", StringComparison.Ordinal))
                {
                    if (AdvertiseAuthentication) { await writer.WriteLineAsync("250-localhost\r\n250 AUTH PLAIN"); }
                    else { await writer.WriteLineAsync("250 localhost"); }
                }
                else if (line.StartsWith("AUTH PLAIN", StringComparison.Ordinal))
                {
                    AuthenticationAttempts++;
                    await writer.WriteLineAsync(AcceptAuthentication ? "235 authenticated" : "535 authentication rejected");
                }
                else if (Outcome == "recipient-reject" && line.StartsWith("RCPT TO:<second@", StringComparison.Ordinal)) { await writer.WriteLineAsync("550 recipient rejected"); }
                else if (line == "DATA")
                {
                    await writer.WriteLineAsync("354 send data");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync(stop.Token) is { } body && body != ".")
                    {
                        data.AppendLine(body.StartsWith("..", StringComparison.Ordinal) ? body[1..] : body);
                    }
                    lock (Messages) { Messages.Add(data.ToString()); }
                    Received.TrySetResult();
                    if (Outcome == "hold") { await ReleaseReply.Task.WaitAsync(stop.Token); }
                    if (Outcome == "drop") { return; }
                    await writer.WriteLineAsync(Outcome == "reject" ? "550 rejected" : "250 accepted");
                }
                else if (line == "QUIT") { await writer.WriteLineAsync("221 goodbye"); return; }
                else { await writer.WriteLineAsync("250 OK"); }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await accept;
        listener.Stop();
        try { await Task.WhenAll(connections); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        stop.Dispose();
    }
}
