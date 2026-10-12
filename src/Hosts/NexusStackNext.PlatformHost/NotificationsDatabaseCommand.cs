using System.Data.Common;
using NexusStackNext.Notifications.Infrastructure;

namespace NexusStackNext.PlatformHost;

internal static class NotificationsDatabaseCommand
{
    public static async Task<int> RunAsync()
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Notifications");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("Notifications migration requires ConnectionStrings__Notifications.");
            return 1;
        }
        try
        {
            await NotificationsDatabase.MigrateAsync(connection).ConfigureAwait(false);
            Console.WriteLine("Notifications migrations applied.");
            return 0;
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine("Notifications migration failed. Check database access and migration compatibility.");
            return 1;
        }
    }
}
