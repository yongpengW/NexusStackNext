using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.Files.Application;
using NexusStackNext.Files.Contracts;
using NexusStackNext.Files.Domain.Stored;
using NexusStackNext.Files.Infrastructure;
using NexusStackNext.Gateway.Routing;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

[Collection(JourneyDatabaseDefinition.Name)]
public sealed class AuditExportDownloadAccessTests(JourneyDatabaseTemplates databases)
{
    [PostgresFact]
    public async Task Original_audit_file_requires_export_permission_after_revocation_and_process_restart_with_the_same_token()
    {
        await using var database = await databases.CreateAsync();
        using var certificates = new GeneratedFileCertificates();
        var settings = new Dictionary<string, string>(certificates.Settings)
        {
            ["Files__Producer__Certificates__0__Producer"] = "auditing",
            ["Kestrel__Endpoints__Public__Url"] = "http://127.0.0.1:0",
            ["Kestrel__Endpoints__Producer__Url"] = "https://127.0.0.1:0",
        };
        await using var platform = await PlatformHostProcess.StartAsync(database.ConnectionString, "audit-export-root-password", settings: settings);
        Assert.NotNull(platform.HttpsAddress);
        await using var gateway = new GatewayHttpApp(platform.Client.BaseAddress!.AbsoluteUri)
        {
            SigningKey = "integration-test-signing-key-long-enough-for-hs256",
            SessionAuthorityAddress = platform.Client.BaseAddress.AbsoluteUri,
        };
        var shipped = GatewayRouteTable.FromJson(await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "routes.json"))).Value;
        gateway.UseRoutes(shipped.Routes.Select(route => route with { ClusterId = "backend", RateLimitPolicy = null }));
        using var root = gateway.CreateClient();
        using var owner = gateway.CreateClient();
        using var other = gateway.CreateClient();
        using var anonymous = gateway.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(root, "journey-root", "audit-export-root-password");
        var user = await CreateAsync(root, "/api/identity/users", new { userName = "audit-file-owner", password = "audit-file-owner-password" });
        _ = await CreateAsync(root, "/api/identity/users", new { userName = "other-file-owner", password = "other-file-owner-password" });
        await PlatformSettingsAccessTests.LoginAsync(other, "other-file-owner", "other-file-owner-password");
        var readMenu = (await CreateAsync(root, "/api/identity/menus", new { title = "Audit investigation", sortOrder = 1 })).GetProperty("menuId").GetString();
        var exportMenu = (await CreateAsync(root, "/api/identity/menus", new { title = "Private audit export", sortOrder = 2 })).GetProperty("menuId").GetString();
        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/auditing/entries", method = "GET", menuId = readMenu });
        _ = await CreateAsync(root, "/api/identity/api-resources", new { path = "/api/auditing/exports", method = "POST", menuId = exportMenu });
        var roleId = (await CreateAsync(root, "/api/identity/roles", new { code = "audit-file-reader", name = "Audit file reader" })).GetProperty("roleId").GetString();
        using (var granted = await root.PostAsync(Path($"/api/identity/roles/{roleId}/menus/{readMenu}"), null)) { Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode); }
        using (var assigned = await root.PostAsync(Path($"/api/identity/users/{user.GetProperty("userId").GetString()}/roles/{roleId}"), null))
        { Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode); }
        await PlatformSettingsAccessTests.LoginAsync(owner, "audit-file-owner", "audit-file-owner-password");
        var ownerId = new JwtSecurityTokenHandler().ReadJwtToken(owner.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        using var producer = new HttpClient(certificates.CreateHandler(certificates.Producer)) { BaseAddress = platform.HttpsAddress };
        var uploadId = Guid.NewGuid();
        var description = new GeneratedFileDescriptionV1(ownerId, Guid.NewGuid(),
            "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", 3, "csv", 1, 1);
        using var registered = await producer.PostAsJsonAsync(Path($"/internal/files/v1/uploads/{uploadId}"), description);
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        var registeredData = await registered.Content.ReadApiDataAsync();
        Assert.Equal("auditing", registeredData.GetProperty("producer").GetString());
        var fileId = registeredData.GetProperty("fileId").GetString();
        using (var content = new ByteArrayContent([1, 2, 3]))
        using (var sealedFile = await producer.PutAsync(Path($"/internal/files/v1/uploads/{uploadId}/content"), content))
        { Assert.Equal(HttpStatusCode.Created, sealedFile.StatusCode); }
        using (var hidden = await owner.GetAsync(Path($"/api/files/{fileId}"))) { Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); }
        using (var published = await producer.PostAsJsonAsync(Path($"/internal/files/v1/uploads/{uploadId}/publish"), new GeneratedFilePublicationV1(Guid.NewGuid())))
        { Assert.Equal(HttpStatusCode.OK, published.StatusCode); }
        using (var query = await owner.GetAsync(Path("/api/auditing/entries"))) { Assert.Equal(HttpStatusCode.OK, query.StatusCode); }
        using (var queryOnly = await owner.GetAsync(Path($"/api/files/{fileId}"))) { Assert.Equal(HttpStatusCode.Forbidden, queryOnly.StatusCode); }
        using (var foreignRoot = await root.GetAsync(Path($"/api/files/{fileId}"))) { Assert.Equal(HttpStatusCode.NotFound, foreignRoot.StatusCode); }
        using (var foreign = await other.GetAsync(Path($"/api/files/{fileId}"))) { Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); }
        using (var unsigned = await anonymous.GetAsync(Path($"/api/files/{fileId}"))) { Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode); }
        using (var granted = await root.PostAsync(Path($"/api/identity/roles/{roleId}/menus/{exportMenu}"), null)) { Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode); }
        await AssertBytesAsync(owner, fileId);

        using (var state = await root.GetAsync(Path($"/api/identity/roles/{roleId}")))
        {
            var version = (await state.Content.ReadApiDataAsync()).GetProperty("version").GetString();
            using var withdrawn = await root.PutAsJsonAsync(Path($"/api/identity/roles/{roleId}/menus"), new { expectedVersion = version, menuIds = new[] { readMenu } });
            Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
        }
        using (var query = await owner.GetAsync(Path("/api/auditing/entries"))) { Assert.Equal(HttpStatusCode.OK, query.StatusCode); }
        using (var forged = new HttpRequestMessage(HttpMethod.Get, Path($"/api/files/{fileId}?producer=pricing&permissionKey=/api/auditing/entries:GET&allow=true")))
        {
            forged.Headers.Add("X-File-Producer", "pricing");
            using var denied = await owner.SendAsync(forged);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        await platform.CrashAsync();
        settings["Kestrel__Endpoints__Public__Url"] = platform.Client.BaseAddress.AbsoluteUri;
        await using var restarted = await PlatformHostProcess.StartAsync(database.ConnectionString, filesRoot: platform.FilesRoot, settings: settings);
        await BusinessProcess.WaitForIdentityForwardingAsync(root, HttpStatusCode.OK);
        using (var persisted = await owner.GetAsync(Path($"/api/files/{fileId}"))) { Assert.Equal(HttpStatusCode.Forbidden, persisted.StatusCode); }
        using (var granted = await root.PostAsync(Path($"/api/identity/roles/{roleId}/menus/{exportMenu}"), null)) { Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode); }
        await AssertBytesAsync(owner, fileId);
        using (var logout = await owner.PostAsync(Path("/api/identity/logout"), null)) { Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode); }
        using (var revoked = await owner.GetAsync(Path($"/api/files/{fileId}"))) { Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode); }
    }

    [Theory]
    [InlineData("denied", HttpStatusCode.Forbidden, null)]
    [InlineData("invalid", HttpStatusCode.Unauthorized, "identity.session.invalid")]
    [InlineData("unavailable", HttpStatusCode.ServiceUnavailable, "identity.session.unavailable")]
    public async Task Rejected_audit_download_releases_storage_without_reading_bytes(string outcome, HttpStatusCode status, string? errorCode)
    {
        var access = new DownloadAccess(outcome);
        ObservedFileStore? store = null;
        await using var baseApp = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IRequestAccessValidator>(access);
            services.AddSingleton<IFileStore>(provider => store = new ObservedFileStore(provider.GetRequiredService<LocalDiskFileStore>()));
        }));
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        var id = await CreatePublishedAsync(app.Services, owner, "auditing");
        using var response = await client.GetAsync(Path($"/api/files/{id}"));
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("/api/auditing/exports:POST", access.RequestedPermission?.Value);
        if (errorCode is not null)
        {
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(errorCode, error.GetProperty("errorCode").GetString());
        }
        Assert.NotNull(store?.Opened);
        await store.Opened.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, store.Opened.Reads);
    }

    [Fact]
    public async Task Cancelled_authority_read_releases_the_opened_file_without_reading_bytes()
    {
        var access = new DownloadAccess("wait");
        ObservedFileStore? store = null;
        await using var baseApp = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IRequestAccessValidator>(access);
            services.AddSingleton<IFileStore>(provider => store = new ObservedFileStore(provider.GetRequiredService<LocalDiskFileStore>()));
        }));
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        var id = await CreatePublishedAsync(app.Services, owner, "auditing");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var download = client.GetAsync(Path($"/api/files/{id}"), cancellation.Token);
        await access.Entered.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Equal("/api/auditing/exports:POST", access.RequestedPermission?.Value);
        Assert.NotNull(store?.Opened);
        await store.Opened.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, store.Opened.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_uploads_and_pricing_outputs_do_not_require_audit_export_permission(bool pricing)
    {
        var access = new DownloadAccess("invalid");
        await using var baseApp = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        await using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IRequestAccessValidator>(access)));
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var owner = new JwtSecurityTokenHandler().ReadJwtToken(client.DefaultRequestHeaders.Authorization!.Parameter!).Subject;
        long id;
        if (pricing) { id = await CreatePublishedAsync(app.Services, owner, "pricing"); }
        else
        {
            using var content = new ByteArrayContent([1, 2, 3]);
            using var uploaded = await client.PostAsync(Path("/api/files?name=ordinary.bin"), content);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            id = (await uploaded.Content.ReadApiDataAsync()).GetProperty("fileId").ReadHttpInt64();
        }
        await AssertBytesAsync(client, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(access.Entered.Task.IsCompleted);
    }

    private static async Task<long> CreatePublishedAsync(IServiceProvider services, string owner, string producer)
    {
        await using var scope = services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<GeneratedFileService>();
        var uploadId = Guid.NewGuid();
        var description = new GeneratedFileDescriptionV1(owner, Guid.NewGuid(),
            "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", 3, "csv", 1, 1);
        var registered = await files.RegisterAsync(producer, uploadId, description);
        Assert.True(registered.IsSuccess, registered.Error?.Code);
        using var content = new MemoryStream([1, 2, 3]);
        Assert.True((await files.SealAsync(producer, uploadId, content)).IsSuccess);
        Assert.True((await files.PublishAsync(producer, uploadId, Guid.NewGuid())).IsSuccess);
        return registered.Value.FileId;
    }

    private static async Task AssertBytesAsync(HttpClient client, string? fileId)
    {
        using var response = await client.GetAsync(Path($"/api/files/{fileId}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoStore);
    }

    private static async Task<JsonElement> CreateAsync<T>(HttpClient client, string path, T input)
    {
        using var response = await client.PostAsJsonAsync(Path(path), input);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadApiDataAsync();
    }

    private static Uri Path(string path) => new(path, UriKind.Relative);

}
