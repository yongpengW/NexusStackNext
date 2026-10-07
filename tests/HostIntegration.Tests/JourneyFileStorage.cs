using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace NexusStackNext.HostIntegration.Tests;

// Factories own their fallback storage. Explicit journey roots remain owned by their caller.
internal sealed class JourneyFileStorage : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "nsn-journey-files-" + Guid.NewGuid().ToString("N"));

    internal void Configure(IHostBuilder builder, string? explicitRoot = null)
    {
        builder.ConfigureHostConfiguration(configuration =>
        {
            var configuredRoot = configuration.Build()["Files:StorageRoot"];
            if (explicitRoot is not null || string.IsNullOrWhiteSpace(configuredRoot))
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Files:StorageRoot"] = explicitRoot ?? Root,
                });
            }
        });
    }

    public void Dispose()
    {
        // Root is generated here, never copied from configuration or supplied by another fixture.
        if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
    }
}
