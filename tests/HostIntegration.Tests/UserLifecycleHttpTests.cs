using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.Identity.Application;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class UserLifecycleHttpTests
{
    private static Uri Path(string value) => new(value, UriKind.Relative);

    [Fact]
    public async Task Invalid_directory_and_conditional_command_inputs_are_rejected_before_mutating_a_user()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var admin = app.CreateClient();
        await LoginAsync(admin, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        foreach (var path in new[] { "/api/identity/users?limit=0", "/api/identity/users?afterUserId=-1", "/api/identity/users/0" })
        {
            using var response = await admin.GetAsync(Path(path));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var body in new object[] { new { }, new { expectedVersion = 0 }, new { expectedVersion = -1 } })
        {
            using var response = await admin.PostAsJsonAsync(Path("/api/identity/users/1/enable"), body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var notFound = await admin.GetAsync(Path("/api/identity/users/9223372036854775807"));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task Concurrent_memory_commands_with_one_observed_version_commit_one_rotation()
    {
        await using var app = new PlatformApp { SchedulingWorkerEnabled = false };
        await using var setup = app.Services.CreateAsyncScope();
        var sender = setup.ServiceProvider.GetRequiredService<ISender>();
        var created = await sender.SendAsync(new CreateUserCommand("memory-competing", "lifecycle-user-password"));
        Assert.True(created.IsSuccess);
        using var start = new Barrier(2);
        async Task<NexusStackNext.BuildingBlocks.Domain.Result> ChangeAsync(string password)
        {
            await using var scope = app.Services.CreateAsyncScope();
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            return await scope.ServiceProvider.GetRequiredService<ISender>().SendAsync(new ResetUserPasswordCommand(created.Value, 1, password));
        }
        var results = await Task.WhenAll(Task.Run(() => ChangeAsync("first-admin-password")), Task.Run(() => ChangeAsync("second-admin-password")));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal("identity.user.conflict", Assert.Single(results, result => result.IsFailure).Error.Code);
        await using var observer = app.Services.CreateAsyncScope();
        var currentSender = observer.ServiceProvider.GetRequiredService<ISender>();
        var view = await currentSender.QueryAsync(new GetUserQuery(created.Value));
        Assert.True(view.IsSuccess);
        Assert.Equal(2, view.Value.Version);
        Assert.True((await currentSender.QueryAsync(new GetCurrentSessionQuery(created.Value, 0))).IsFailure);
        Assert.True((await currentSender.QueryAsync(new GetCurrentSessionQuery(created.Value, 1))).IsSuccess);
    }

    [Fact]
    public async Task Memory_fact_capacity_rejection_rolls_back_disable_and_password_rotation_together_with_session_state()
    {
        await using var app = new MemoryFactCapacityApp("Identity", 3) { SchedulingWorkerEnabled = false };
        using var client = app.CreateClient();
        using var created = await client.PostAsJsonAsync(Path("/api/identity/users"), new { userName = "capacity-lifecycle", password = "lifecycle-user-password" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadApiDataAsync()).GetProperty("userId").ReadHttpInt64();
        await LoginAsync(client, "capacity-lifecycle", "lifecycle-user-password");
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var disabled = await sender.SendAsync(new SetUserEnabledCommand(id, 2, false));
        Assert.True(disabled.IsFailure);
        Assert.Equal("identity.audit_capacity.exhausted", disabled.Error.Code);
        var reset = await sender.SendAsync(new ResetUserPasswordCommand(id, 2, "replacement-password"));
        Assert.True(reset.IsFailure);
        Assert.Equal("identity.audit_capacity.exhausted", reset.Error.Code);
        using var current = await client.GetAsync(Path("/api/identity/me"));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var view = await current.Content.ReadApiDataAsync();
        Assert.True(view.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(2, view.GetProperty("version").ReadHttpInt64());
        using var original = await client.PostAsJsonAsync(Path("/api/identity/me/password"),
            new { expectedVersion = 2, oldPassword = "lifecycle-user-password", newPassword = "lifecycle-user-password" });
        Assert.Equal(HttpStatusCode.NoContent, original.StatusCode);
    }

    [Fact]
    public async Task Root_seed_refuses_a_normal_user_collision_and_keeps_the_existing_credentials()
    {
        await using var app = new PlatformApp { SchedulingWorkerEnabled = false };
        await using var scope = app.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var created = await sender.SendAsync(new CreateUserCommand("root-collision", "original-user-password"));
        Assert.True(created.IsSuccess);
        var seeded = await sender.SendAsync(new SeedRootAccountCommand("root-collision", "replacement-root-password"));
        Assert.True(seeded.IsFailure);
        Assert.Equal("identity.root.name_collision", seeded.Error.Code);
        var loggedIn = await sender.SendAsync(new LoginCommand("root-collision", "original-user-password", null));
        Assert.True(loggedIn.IsSuccess);
        var current = await sender.QueryAsync(new GetCurrentSessionQuery(created.Value, 0));
        Assert.True(current.IsSuccess);
        Assert.False(current.Value.IsRoot);
    }

    [Fact]
    public async Task Own_password_rotation_verifies_old_password_and_same_plaintext_is_a_noop()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var admin = app.CreateClient();
        using var ordinary = app.CreateClient();
        using var created = await ordinary.PostAsJsonAsync(Path("/api/identity/users"), new { userName = "password-user", password = "lifecycle-user-password" });
        var id = (await created.Content.ReadApiDataAsync()).GetProperty("userId").ReadHttpInt64();
        var old = await LoginAsync(ordinary, "password-user", "lifecycle-user-password");
        await LoginAsync(admin, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var wrong = await ordinary.PostAsJsonAsync(Path("/api/identity/me/password"), new { expectedVersion = 2, oldPassword = "wrong-old-password", newPassword = "replacement-password" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var shortPassword = await ordinary.PostAsJsonAsync(Path("/api/identity/me/password"), new { expectedVersion = 2, oldPassword = "lifecycle-user-password", newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
        using var noop = await ordinary.PostAsJsonAsync(Path("/api/identity/me/password"), new { expectedVersion = 2, oldPassword = "lifecycle-user-password", newPassword = "lifecycle-user-password" });
        Assert.Equal(HttpStatusCode.NoContent, noop.StatusCode);
        using var current = await ordinary.GetAsync(Path("/api/identity/me"));
        Assert.Equal(2, (await current.Content.ReadApiDataAsync()).GetProperty("version").ReadHttpInt64());
        using var rotated = await ordinary.PostAsJsonAsync(Path("/api/identity/me/password"), new { userId = 1, expectedVersion = 2, oldPassword = "lifecycle-user-password", newPassword = "replacement-password" });
        Assert.Equal(HttpStatusCode.NoContent, rotated.StatusCode);
        using var invalidated = await ordinary.GetAsync(Path("/api/identity/me"));
        Assert.Equal(HttpStatusCode.Unauthorized, invalidated.StatusCode);
        using var oldRefresh = await ordinary.PostAsJsonAsync(Path("/api/identity/refresh"), new { refreshToken = old.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, oldRefresh.StatusCode);
        using var changed = await admin.GetAsync(Path($"/api/identity/users/{id}"));
        Assert.Equal(3, (await changed.Content.ReadApiDataAsync()).GetProperty("version").ReadHttpInt64());
        await LoginAsync(ordinary, "password-user", "replacement-password");
        using var deniedReset = await ordinary.PostAsJsonAsync(Path($"/api/identity/users/{id}/password"), new { expectedVersion = 4, newPassword = "another-password" });
        Assert.Equal(HttpStatusCode.Forbidden, deniedReset.StatusCode);
        using var staleReset = await admin.PostAsJsonAsync(Path($"/api/identity/users/{id}/password"), new { expectedVersion = 3, newPassword = "another-password" });
        Assert.Equal(HttpStatusCode.Conflict, staleReset.StatusCode);
        using var sameReset = await admin.PostAsJsonAsync(Path($"/api/identity/users/{id}/password"), new { expectedVersion = 4, newPassword = "replacement-password" });
        Assert.Equal(HttpStatusCode.NoContent, sameReset.StatusCode);
        using var reset = await admin.PostAsJsonAsync(Path($"/api/identity/users/{id}/password"), new { expectedVersion = 4, newPassword = "admin-replacement-password" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        using var deniedAfterReset = await ordinary.GetAsync(Path("/api/identity/me"));
        Assert.Equal(HttpStatusCode.Unauthorized, deniedAfterReset.StatusCode);
        await LoginAsync(ordinary, "password-user", "admin-replacement-password");
        using var oldLogin = await ordinary.PostAsJsonAsync(Path("/api/identity/login"), new { userName = "password-user", password = "lifecycle-user-password" });
        Assert.Equal(HttpStatusCode.BadRequest, oldLogin.StatusCode);
    }

    [Fact]
    public async Task Disable_and_enable_require_management_permission_and_current_version_and_revoke_old_sessions()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var admin = app.CreateClient();
        using var ordinary = app.CreateClient();
        using var created = await ordinary.PostAsJsonAsync(Path("/api/identity/users"), new { userName = "lifecycle-user", password = "lifecycle-user-password" });
        var id = (await created.Content.ReadApiDataAsync()).GetProperty("userId").ReadHttpInt64();
        var old = await LoginAsync(ordinary, "lifecycle-user", "lifecycle-user-password");
        using var denied = await ordinary.PostAsJsonAsync(Path($"/api/identity/users/{id}/disable"), new { expectedVersion = 2 });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await LoginAsync(admin, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var disabled = await admin.PostAsJsonAsync(Path($"/api/identity/users/{id}/disable"), new { expectedVersion = 2 });
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        using var stale = await admin.PostAsJsonAsync(Path($"/api/identity/users/{id}/enable"), new { expectedVersion = 2 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var noop = await admin.PostAsJsonAsync(Path($"/api/identity/users/{id}/disable"), new { expectedVersion = 3 });
        Assert.Equal(HttpStatusCode.NoContent, noop.StatusCode);
        using var enabled = await admin.PostAsJsonAsync(Path($"/api/identity/users/{id}/enable"), new { expectedVersion = 3 });
        Assert.Equal(HttpStatusCode.NoContent, enabled.StatusCode);
        using var mine = await ordinary.GetAsync(Path("/api/identity/me"));
        Assert.Equal(HttpStatusCode.Unauthorized, mine.StatusCode);
        using var oldRefresh = await ordinary.PostAsJsonAsync(Path("/api/identity/refresh"), new { refreshToken = old.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, oldRefresh.StatusCode);
        await LoginAsync(ordinary, "lifecycle-user", "lifecycle-user-password");
        using var fresh = await ordinary.GetAsync(Path("/api/identity/me"));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        using var root = await admin.GetAsync(Path("/api/identity/me"));
        var rootView = await root.Content.ReadApiDataAsync();
        using var rootDisable = await admin.PostAsJsonAsync(Path($"/api/identity/users/{rootView.GetProperty("userId").ReadHttpInt64()}/disable"), new { expectedVersion = rootView.GetProperty("version").ReadHttpInt64(), isBuiltIn = false });
        Assert.Equal(HttpStatusCode.BadRequest, rootDisable.StatusCode);
    }

    [Fact]
    public async Task User_directory_is_bounded_and_private_while_own_profile_is_available()
    {
        await using var app = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        using var admin = app.CreateClient();
        using var ordinary = app.CreateClient();
        using var registered = await ordinary.PostAsJsonAsync(Path("/api/identity/users"),
            new { userName = "directory-user", password = "directory-user-password" });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var id = (await registered.Content.ReadApiDataAsync()).GetProperty("userId").ReadHttpInt64();
        await LoginAsync(admin, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var first = await admin.GetAsync(Path("/api/identity/users?limit=1"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var page = await first.Content.ReadApiDataAsync();
        Assert.Single(page.GetProperty("items").EnumerateArray());
        var after = page.GetProperty("nextAfterUserId").ReadHttpInt64();
        using var next = await admin.GetAsync(Path($"/api/identity/users?limit=1&afterUserId={after}"));
        var nextPage = await next.Content.ReadApiDataAsync();
        var second = Assert.Single(nextPage.GetProperty("items").EnumerateArray());
        Assert.True(second.GetProperty("userId").ReadHttpInt64() > after);
        Assert.Equal(JsonValueKind.Null, nextPage.GetProperty("nextAfterUserId").ValueKind);
        using var unbounded = await admin.GetAsync(Path("/api/identity/users?limit=101"));
        Assert.Equal(HttpStatusCode.BadRequest, unbounded.StatusCode);
        await LoginAsync(ordinary, "directory-user", "directory-user-password");
        using var deniedList = await ordinary.GetAsync(Path("/api/identity/users"));
        Assert.Equal(HttpStatusCode.Forbidden, deniedList.StatusCode);
        using var deniedDetail = await ordinary.GetAsync(Path($"/api/identity/users/{id}"));
        Assert.Equal(HttpStatusCode.Forbidden, deniedDetail.StatusCode);
        using var mine = await ordinary.GetAsync(Path("/api/identity/me"));
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        var profile = await mine.Content.ReadApiDataAsync();
        Assert.Equal(id, profile.GetProperty("userId").ReadHttpInt64());
        Assert.Equal(["isEnabled", "roleIds", "userId", "userName", "version"],
            profile.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    internal static async Task<JsonElement> LoginAsync(HttpClient client, string name, string password)
    {
        using var response = await client.PostAsJsonAsync(Path("/api/identity/login"), new { userName = name, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.Content.ReadApiDataAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.GetProperty("accessToken").GetString());
        return data;
    }
}
