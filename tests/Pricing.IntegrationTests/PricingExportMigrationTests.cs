using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Pricing.Application;
using NexusStackNext.Pricing.Infrastructure;
using NexusStackNext.TestSupport;
using Npgsql;

namespace NexusStackNext.Pricing.IntegrationTests;

public sealed class PricingExportMigrationTests(PricingDatabaseFixture database) : IClassFixture<PricingDatabaseFixture>, IAsyncLifetime
{
    private const string PreviousMigration = "20261005060012_ConditionalFactRecovery";
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task Accepted_export_history_blocks_down_migration_even_after_owner_cancels()
    {
        var services = new ServiceCollection().AddNexusStackApplication();
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser("export-owner"));
        services.AddPricingPostgres(database.ConnectionString).AddPricingExportPersistence();
        await using var app = services.BuildServiceProvider();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var accepted = (await sender.SendAsync(new AcceptPricingExport(Guid.NewGuid(), [item]))).Value;
        var canceled = (await sender.SendAsync(new CancelPricingExport(accepted.ExportId, accepted.Version))).Value;
        await using var migration = CreateMigrationContext();
        var rejected = await Assert.ThrowsAsync<PostgresException>(() => migration.GetService<IMigrator>().MigrateAsync(PreviousMigration));
        Assert.Equal("P0001", rejected.SqlState);
        Assert.Contains("pricing_exports_history_retained", rejected.MessageText, StringComparison.Ordinal);
        Assert.Equal(canceled, (await sender.QueryAsync(new GetPricingExport(accepted.ExportId))).Value);
        Assert.True(await PricingDatabase.IsReadyAsync(database.ConnectionString));
    }

    [PostgresFact]
    public async Task Empty_export_migration_can_roll_back_and_reinitialize_without_losing_existing_quote()
    {
        var services = new ServiceCollection().AddNexusStackApplication();
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser("export-owner"));
        services.AddPricingPostgres(database.ConnectionString).AddPricingExportPersistence();
        await using var app = services.BuildServiceProvider();
        await using var scope = app.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var item = Guid.NewGuid();
        Assert.True((await sender.SendAsync(new UpdatePricingCost(Guid.NewGuid(), item, 0, 80m, 0.2m))).IsSuccess);
        var original = (await sender.QueryAsync(new GetPriceQuote(item))).Value;
        await using (var migration = CreateMigrationContext()) { await migration.GetService<IMigrator>().MigrateAsync(PreviousMigration); }
        Assert.False(await PricingDatabase.IsReadyAsync(database.ConnectionString));
        await PricingDatabase.MigrateAsync(database.ConnectionString);
        Assert.Equal(original, (await sender.QueryAsync(new GetPriceQuote(item))).Value);
        Assert.True(await PricingDatabase.IsReadyAsync(database.ConnectionString));
    }

    private DbContext CreateMigrationContext()
    {
        // 与 dotnet ef 相同的标准设计时接口；不穿透导出处理器、仓储或领域方法。
        var factoryType = typeof(PricingDatabase).Assembly.GetTypes().Single(type => type.IsAssignableTo(typeof(IDesignTimeDbContextFactory<DbContext>)));
        var factory = (IDesignTimeDbContextFactory<DbContext>)Activator.CreateInstance(factoryType, nonPublic: true)!;
        var previous = Environment.GetEnvironmentVariable("ConnectionStrings__Pricing");
        try
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Pricing", database.ConnectionString);
            return factory.CreateDbContext([]);
        }
        finally { Environment.SetEnvironmentVariable("ConnectionStrings__Pricing", previous); }
    }
}
