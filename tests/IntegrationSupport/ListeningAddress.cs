using System.Text;

namespace NexusStackNext.IntegrationSupport;

// The child binds port zero itself; its lifetime message reports the socket it actually owns.
internal sealed class ListeningAddress
{
    private readonly TaskCompletionSource<Uri> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Uri? Address => _bound.Task.IsCompletedSuccessfully ? _bound.Task.Result : null;

    internal async Task<string> CaptureAsync(StreamReader output)
    {
        var captured = new StringBuilder();
        while (await output.ReadLineAsync() is { } line)
        {
            captured.AppendLine(line);
            const string marker = "Now listening on: ";
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && Uri.TryCreate(line[(index + marker.Length)..].Trim(), UriKind.Absolute, out var address)
                && address.Scheme == "http" && address.Host == "127.0.0.1" && address.Port > 0
                && address.AbsolutePath == "/" && address.Query.Length == 0 && address.Fragment.Length == 0 && address.UserInfo.Length == 0)
            {
                _bound.TrySetResult(address);
            }
        }
        return captured.ToString();
    }
}
