using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class PrivateFilesAccessTests
{
    [Fact]
    public async Task RootAccount_CannotReadAnotherOwnersFileOrDeletionStatus()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nsn-private-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = new PrivateFilesApp(directory, rootAccount: true);
            app.UseKestrel(0);
            using var owner = app.CreateClient();
            using var root = app.CreateClient();
            await RegisterAndLoginAsync(owner, "private-owner");
            await PlatformSettingsAccessTests.LoginAsync(root, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
            using var content = new ByteArrayContent([1, 2]);
            using var uploaded = await owner.PostAsync(new Uri("/api/files?name=private.bin", UriKind.Relative), content);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            var id = (await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").GetInt64();
            foreach (var (method, path) in new[]
            {
                (HttpMethod.Get, $"/api/files/{id}"),
                (HttpMethod.Get, $"/api/files/{id}/metadata"),
                (HttpMethod.Delete, $"/api/files/{id}"),
            })
            {
                using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
                using var forbidden = await root.SendAsync(request);
                Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
            }
            using var deleted = await owner.DeleteAsync(new Uri($"/api/files/{id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            using var otherStatus = await root.GetAsync(new Uri($"/api/files/{id}/deletion", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, otherStatus.StatusCode);
            using var ownStatus = await owner.GetAsync(new Uri($"/api/files/{id}/deletion", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, ownStatus.StatusCode);
            Assert.True((await ownStatus.Content.ReadApiDataAsync()).GetProperty("completed").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    [Fact]
    public async Task ConfiguredUploadSizeAboveKestrelDefault_IsUsable()
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-private-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = new PrivateFilesApp(root);
            app.UseKestrel(0);
            using var addressClient = app.CreateClient();
            using var client = new HttpClient { BaseAddress = addressClient.BaseAddress };
            await RegisterAndLoginAsync(client, "large-file-owner");
            using var content = new ByteArrayContent(new byte[31 * 1024 * 1024]);
            using var uploaded = await client.PostAsync(new Uri("/api/files?name=large.bin", UriKind.Relative), content);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            Assert.Equal(31 * 1024 * 1024, (await uploaded.Content.ReadApiDataAsync()).GetProperty("size").GetInt64());
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Theory]
    [InlineData("bad\r\nname.txt", "text/plain", "files.file_name.unsafe")]
    [InlineData("normal.txt", "not-a-media-type", "files.content_type.invalid")]
    [InlineData("normal.txt", "text/*", "files.content_type.invalid")]
    public async Task UnsafeUploadMetadata_IsRejectedBeforeWritingBytes(string name, string contentType, string errorCode)
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-private-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = new PrivateFilesApp(root);
            app.UseKestrel(0);
            using var client = app.CreateClient();
            await RegisterAndLoginAsync(client, "metadata-file-owner");
            using var content = new ByteArrayContent([1, 2]);
            Assert.True(content.Headers.TryAddWithoutValidation("Content-Type", contentType));
            using var response = await client.PostAsync(new Uri("/api/files?name=" + Uri.EscapeDataString(name), UriKind.Relative), content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal(errorCode, error.GetProperty("errorCode").GetString());
            Assert.Empty(Directory.EnumerateFiles(root, "v1-*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact]
    public async Task ConcurrentUploadLimit_ReleasesCapacityAfterCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-private-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = new PrivateFilesApp(root, maxConcurrentUploads: 1);
            app.UseKestrel(0);
            using var addressClient = app.CreateClient();
            // 工厂的重定向处理器会先复制完整请求体；暂停上传需要直接使用真实网络客户端。
            using var client = new HttpClient { BaseAddress = addressClient.BaseAddress, Timeout = TimeSpan.FromSeconds(20) };
            await RegisterAndLoginAsync(client, "concurrent-file-owner");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var slowContent = new PausedUploadContent(cancellation.Token);
            var slow = client.PostAsync(new Uri("/api/files?name=cancelled.bin", UriKind.Relative), slowContent, cancellation.Token);
            try
            {
                // 临时存储中的写入建立“首个请求已进入存储”的屏障；结果仍通过 HTTP 验证。
                while (!Directory.EnumerateFiles(root, "v1-*", SearchOption.AllDirectories).Any())
                {
                    await Task.Delay(20, cancellation.Token);
                }
                using var competingContent = new ByteArrayContent([9]);
                using var competing = await client.PostAsync(new Uri("/api/files?name=competing.bin", UriKind.Relative), competingContent);
                Assert.Equal(HttpStatusCode.TooManyRequests, competing.StatusCode);
            }
            finally
            {
                await cancellation.CancelAsync();
                try { using var result = await slow; }
                catch (OperationCanceledException) { }
                catch (HttpRequestException) { }
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Directory.EnumerateFiles(root, "v1-*", SearchOption.AllDirectories).Any())
            {
                await Task.Delay(20, timeout.Token);
            }
            using var nextContent = new ByteArrayContent([1, 2]);
            using var next = await client.PostAsync(new Uri("/api/files?name=after-cancellation.bin", UriKind.Relative), nextContent);
            Assert.Equal(HttpStatusCode.Created, next.StatusCode);
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedUpload_WithOrWithoutContentLength_IsRejected_AndNextUploadWorks(bool chunked)
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-private-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = new PrivateFilesApp(root, maxBytes: 8);
            app.UseKestrel(0);
            using var client = app.CreateClient();
            await RegisterAndLoginAsync(client, "bounded-file-owner");
            using var oversized = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/files?name=oversized.bin", UriKind.Relative))
            {
                Content = new ByteArrayContent(new byte[9]),
            };
            if (chunked) { oversized.Headers.TransferEncodingChunked = true; }
            using var rejected = await client.SendAsync(oversized);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
            Assert.Empty(Directory.EnumerateFiles(root, "v1-*", SearchOption.AllDirectories));
            using var acceptedContent = new ByteArrayContent([1, 2, 3, 4, 5, 6, 7, 8]);
            using var accepted = await client.PostAsync(new Uri("/api/files?name=bounded.bin", UriKind.Relative), acceptedContent);
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
            var payload = await accepted.Content.ReadApiDataAsync();
            Assert.Equal(8, payload.GetProperty("size").GetInt64());
            using var downloaded = await client.GetAsync(new Uri($"/api/files/{payload.GetProperty("fileId").GetInt64()}", UriKind.Relative));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, await downloaded.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact]
    public async Task LoggedOutSession_CannotUploadOrAccessPreviouslyOwnedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-private-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = new PrivateFilesApp(root);
            app.UseKestrel(0);
            using var client = app.CreateClient();
            await RegisterAndLoginAsync(client, "revoked-file-owner");
            using var content = new ByteArrayContent([1, 2, 3]);
            using var uploaded = await client.PostAsync(new Uri("/api/files?name=private.bin", UriKind.Relative), content);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            var id = (await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").GetInt64();
            using var logout = await client.PostAsync(new Uri("/api/identity/logout", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            foreach (var (method, path) in new[]
            {
                (HttpMethod.Post, "/api/files?name=revoked.bin"),
                (HttpMethod.Get, $"/api/files/{id}"),
                (HttpMethod.Get, $"/api/files/{id}/metadata"),
                (HttpMethod.Get, $"/api/files/{id}/deletion"),
                (HttpMethod.Delete, $"/api/files/{id}"),
            })
            {
                using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
                if (method == HttpMethod.Post) { request.Content = new ByteArrayContent([4, 5, 6]); }
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Theory]
    [InlineData("GET", "/metadata")]
    [InlineData("GET", "")]
    [InlineData("DELETE", "")]
    public async Task AnotherUser_CannotAccessPrivateFile_AndOwnerCanStillDownload(string method, string suffix)
    {
        var root = Path.Combine(Path.GetTempPath(), "nsn-private-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = new PrivateFilesApp(root);
            app.UseKestrel(0);
            using var owner = app.CreateClient();
            using var other = app.CreateClient();
            await RegisterAndLoginAsync(owner, "file-owner");
            await RegisterAndLoginAsync(other, "another-user");
            byte[] bytes = [0, 255, 128, 10, 13, 42];
            using var content = new ByteArrayContent(bytes);
            using var uploaded = await owner.PostAsync(new Uri("/api/files?name=private.bin", UriKind.Relative), content);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            var payload = await uploaded.Content.ReadApiDataAsync();
            Assert.False(payload.TryGetProperty("storageKey", out _));
            var id = payload.GetProperty("fileId").GetInt64();
            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri($"/api/files/{id}{suffix}", UriKind.Relative));
            using var denied = await other.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            using var download = await owner.GetAsync(new Uri($"/api/files/{id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
            Assert.True(download.Headers.CacheControl?.NoStore);
            Assert.True(download.Headers.CacheControl?.Private);
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task RegisterAndLoginAsync(HttpClient client, string userName)
    {
        const string password = "private-files-test-password";
        using var registered = await client.PostAsJsonAsync(new Uri("/api/identity/users", UriKind.Relative), new { userName, password });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await PlatformSettingsAccessTests.LoginAsync(client, userName, password);
    }

    private sealed class PrivateFilesApp(string root, long maxBytes = 64 * 1024 * 1024, int maxConcurrentUploads = 4, bool rootAccount = false) : PlatformApp
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Files:StorageRoot"] = root,
                    ["Files:Upload:MaxBytes"] = maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Files:Upload:MaxConcurrentUploads"] = maxConcurrentUploads.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Identity:Root:UserName"] = rootAccount ? PlatformAppWithRootAccount.RootUserName : null,
                    ["Identity:Root:Password"] = rootAccount ? PlatformAppWithRootAccount.RootPassword : null,
                }));
            return base.CreateHost(builder);
        }
    }

}
