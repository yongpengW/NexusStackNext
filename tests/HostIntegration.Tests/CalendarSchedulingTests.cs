using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.IntegrationSupport;
using NexusStackNext.Scheduling.Application;
using NexusStackNext.TestSupport;

namespace NexusStackNext.HostIntegration.Tests;

public sealed class CalendarSchedulingTests
{
    [Theory]
    [InlineData("FireOnce", 1, "Coalesced")]
    [InlineData("Skip", 0, "Skipped")]
    public async Task LateCalendarWindow_IsDecidedOnce_AndHistorySurvivesRuleChanges(string policy, int triggered, string outcome)
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-10-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await using var rootApp = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        await using var app = rootApp.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TaskRegistry>();
                services.RemoveAll<ScheduleRunner>();
                services.AddScoped(provider => new TaskRegistry(provider.GetRequiredService<IScheduledTaskStore>(), provider.GetRequiredService<IIdGenerator>(), clock,
                    provider.GetRequiredService<IScheduleCalendar>()));
                services.AddScoped(provider => new ScheduleRunner(provider.GetRequiredService<IScheduledTaskStore>(), clock, provider.GetRequiredService<IScheduleCalendar>()));
            });
        });
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
        {
            code = "calendar-late",
            rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "UTC", misfirePolicy = policy },
            targetKind = "costing.recalculate",
            targetId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
        clock.UtcNow = clock.UtcNow.AddMinutes(3);
        await using var scope = app.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<ScheduleRunner>();
        var result = await runner.RunOnceAsync();
        Assert.Equal(triggered, result.Triggered);
        Assert.Empty(result.FailedPlanIds);
        Assert.Equal(0, (await runner.RunOnceAsync()).Examined);
        var plans = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        var plan = Assert.Single(plans.GetProperty("data").EnumerateArray());
        Assert.Equal(2, plan.GetProperty("version").ReadHttpInt64());
        Assert.Equal("2026-10-02T00:04:00+00:00", plan.GetProperty("nextRunAt").GetString());
        var history = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/scheduling/tasks/{id}/occurrences", UriKind.Relative));
        Assert.Equal(triggered, history.GetProperty("data").GetArrayLength());
        using var decisionsResponse = await client.GetAsync(new Uri($"/api/scheduling/tasks/{id}/decisions", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, decisionsResponse.StatusCode);
        var decisions = await decisionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var decision = Assert.Single(decisions.GetProperty("data").EnumerateArray());
        Assert.Equal(outcome, decision.GetProperty("kind").GetString());
        Assert.Equal("2026-10-02T00:01:00+00:00", decision.GetProperty("scheduledAt").GetString());
        Assert.Equal("2026-10-02T00:03:00+00:00", decision.GetProperty("observedAt").GetString());
        Assert.Equal(1, decision.GetProperty("scheduleRevision").ReadHttpInt64());
        Assert.Equal(policy, decision.GetProperty("rule").GetProperty("misfirePolicy").GetString());
        if (triggered == 0) { Assert.Equal(JsonValueKind.Null, decision.GetProperty("occurrenceId").ValueKind); }
        else { Assert.Equal(Assert.Single(history.GetProperty("data").EnumerateArray()).GetProperty("occurrenceId").GetGuid(), decision.GetProperty("occurrenceId").GetGuid()); }

        clock.UtcNow = clock.UtcNow.AddHours(1);
        using var same = await client.PutAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/rule", UriKind.Relative), new
        {
            expectedVersion = "2",
            rule = new { kind = "Cron", expression = "  * *  * * * ", timeZoneId = "UTC", misfirePolicy = policy },
        });
        Assert.Equal(HttpStatusCode.NoContent, same.StatusCode);
        plans = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        Assert.Equal(plan.ToString(), Assert.Single(plans.GetProperty("data").EnumerateArray()).ToString());
        using var changed = await client.PutAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/rule", UriKind.Relative), new
        {
            expectedVersion = "2",
            rule = new { kind = "Cron", expression = "*/5 * * * *", timeZoneId = "Asia/Shanghai" },
        });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        plans = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        plan = Assert.Single(plans.GetProperty("data").EnumerateArray());
        Assert.Equal(3, plan.GetProperty("version").ReadHttpInt64());
        Assert.Equal(2, plan.GetProperty("scheduleRevision").ReadHttpInt64());
        Assert.Equal("2026-10-02T01:05:00+00:00", plan.GetProperty("nextRunAt").GetString());
        using var stale = await client.PutAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/rule", UriKind.Relative), new
        {
            expectedVersion = "2",
            rule = new { kind = "Interval", intervalSeconds = 30 },
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var retained = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/scheduling/tasks/{id}/decisions", UriKind.Relative));
        Assert.Equal(decision.ToString(), Assert.Single(retained.GetProperty("data").EnumerateArray()).ToString());
    }

    [Fact]
    public async Task Preview_IntervalIsExplicit_AndDoesNotAcquireCalendarOrMisfireSemantics()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var response = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/preview", UriKind.Relative), new
        {
            rule = new { kind = "Interval", intervalSeconds = 30.25 },
            after = "2026-10-02T00:00:00Z",
            count = 3,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadApiDataAsync();
        Assert.Equal("Interval", preview.GetProperty("rule").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("rule").GetProperty("misfirePolicy").ValueKind);
        Assert.Equal(new[] { "2026-10-02T00:00:30.25+00:00", "2026-10-02T00:01:00.5+00:00", "2026-10-02T00:01:30.75+00:00" },
            preview.GetProperty("times").EnumerateArray().Select(time => time.GetProperty("utc").GetString()));
        foreach (var rule in new object[]
        {
            new { kind = "Interval", intervalSeconds = 0.5 },
            new { kind = "Interval", intervalSeconds = 31622401 },
            new { kind = "Interval", intervalSeconds = 30, timeZoneId = "UTC" },
            new { kind = "Interval", intervalSeconds = 30, misfirePolicy = "Skip" },
        })
        {
            using var invalid = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/preview", UriKind.Relative), new { rule, after = "2026-10-02T00:00:00Z" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }

    [Fact]
    public async Task Definition_CreatesThePreviewedRule_AndResumeStartsAfterNow()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-10-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await using var rootApp = new PlatformAppWithRootAccount { SchedulingWorkerEnabled = false };
        await using var app = rootApp.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TaskRegistry>();
                services.AddScoped(provider => new TaskRegistry(provider.GetRequiredService<IScheduledTaskStore>(), provider.GetRequiredService<IIdGenerator>(), clock,
                    provider.GetRequiredService<IScheduleCalendar>()));
            });
        });
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var rule = new { kind = "Cron", expression = "10 * 7-23 * * ?", timeZoneId = "Asia/Shanghai", misfirePolicy = "Skip", graceSeconds = 10 };
        using var preview = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/preview", UriKind.Relative), new { rule, after = clock.UtcNow });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var expected = await preview.Content.ReadApiDataAsync();
        using var created = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
        {
            code = "calendar-cost",
            rule,
            targetKind = "costing.recalculate",
            targetId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadApiDataAsync()).GetProperty("taskId").ReadHttpInt64();
        var page = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        var plan = Assert.Single(page.GetProperty("data").EnumerateArray());
        Assert.Equal(expected.GetProperty("rule").ToString(), plan.GetProperty("rule").ToString());
        Assert.Equal("2026-10-02T00:00:10+00:00", plan.GetProperty("nextRunAt").GetString());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("intervalSeconds").ValueKind);
        Assert.Equal(1, plan.GetProperty("scheduleRevision").ReadHttpInt64());
        using var pause = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/pause", UriKind.Relative), new { expectedVersion = "1" });
        Assert.Equal(HttpStatusCode.NoContent, pause.StatusCode);
        clock.UtcNow = clock.UtcNow.AddDays(2);
        using var resume = await client.PostAsJsonAsync(new Uri($"/api/scheduling/tasks/{id}/resume", UriKind.Relative), new { expectedVersion = "2" });
        Assert.Equal(HttpStatusCode.NoContent, resume.StatusCode);
        page = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        plan = Assert.Single(page.GetProperty("data").EnumerateArray());
        Assert.Equal("2026-10-04T00:00:10+00:00", plan.GetProperty("nextRunAt").GetString());
        Assert.Equal(3, plan.GetProperty("version").ReadHttpInt64());
        Assert.Equal(1, plan.GetProperty("scheduleRevision").ReadHttpInt64());

        using var interval = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/", UriKind.Relative), new
        {
            code = "explicit-interval",
            rule = new { kind = "Interval", intervalSeconds = 30 },
            targetKind = "costing.recalculate",
            targetId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Created, interval.StatusCode);
        page = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        var intervalPlan = Assert.Single(page.GetProperty("data").EnumerateArray(), item => item.GetProperty("code").GetString() == "explicit-interval");
        Assert.Equal(clock.UtcNow, intervalPlan.GetProperty("nextRunAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Preview_UsesExplicitDstSemantics_AndExclusiveUtcInstants()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var cases = new[]
        {
            (Cron: "30 2 * * *", Zone: "America/New_York", After: "2026-03-08T06:59:00Z", Times: new[] { "2026-03-08T07:00:00Z", "2026-03-09T06:30:00Z" }),
            (Cron: "30 1 * * *", Zone: "America/New_York", After: "2026-11-01T04:00:00Z", Times: new[] { "2026-11-01T05:30:00Z", "2026-11-02T06:30:00Z" }),
            (Cron: "*/30 * * * *", Zone: "America/New_York", After: "2026-11-01T04:30:00Z", Times: new[] { "2026-11-01T05:00:00Z", "2026-11-01T05:30:00Z", "2026-11-01T06:00:00Z", "2026-11-01T06:30:00Z", "2026-11-01T07:00:00Z" }),
            (Cron: "15 2 * * *", Zone: "Australia/Lord_Howe", After: "2026-10-03T15:00:00Z", Times: new[] { "2026-10-03T15:30:00Z" }),
            (Cron: "0 0 1 1 * ?", Zone: "Asia/Shanghai", After: "2026-09-30T00:00:00Z", Times: new[] { "2026-09-30T17:00:00Z" }),
            (Cron: "0 1 1 * ?", Zone: "Asia/Shanghai", After: "2026-09-30T00:00:00Z", Times: new[] { "2026-09-30T17:00:00Z" }),
            (Cron: "0 0 1 * mon", Zone: "UTC", After: "2026-01-01T00:00:00Z", Times: new[] { "2026-06-01T00:00:00Z" }),
            (Cron: "* * * * *", Zone: "UTC", After: "2026-10-02T00:00:00Z", Times: new[] { "2026-10-02T00:01:00Z" }),
        };
        foreach (var item in cases)
        {
            using var response = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/preview", UriKind.Relative), new
            {
                rule = new { kind = "Cron", expression = item.Cron, timeZoneId = item.Zone },
                after = item.After,
                count = item.Times.Length,
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var preview = await response.Content.ReadApiDataAsync();
            Assert.Equal(item.Cron.ToUpperInvariant(), preview.GetProperty("rule").GetProperty("expression").GetString());
            Assert.Equal(item.Times.Select(value => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
                preview.GetProperty("times").EnumerateArray().Select(time => time.GetProperty("utc").GetDateTimeOffset()));
        }
    }

    [Fact]
    public async Task Preview_RejectsAmbiguousAndUnboundedRules_WithoutSideEffects()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var invalid = new (object Request, string Error)[]
        {
            (new { rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "UTC" }, after = "2026-10-02T00:00:00" }, "scheduling.preview.invalid"),
            (new { rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "UTC" } }, "scheduling.preview.invalid"),
            (new { rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "UTC", intervalSeconds = 30 }, after = "2026-01-01T00:00:00Z" }, "scheduling.rule.invalid"),
            (new { rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "UTC", cronFieldCount = 6 }, after = "2026-01-01T00:00:00Z" }, "scheduling.cron.invalid"),
            (new { rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "UTC", misfirePolicy = "CatchUpAll" }, after = "2026-01-01T00:00:00Z" }, "scheduling.misfire.invalid"),
            (new { rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "UTC", graceSeconds = 0 }, after = "2026-01-01T00:00:00Z" }, "scheduling.misfire.invalid"),
            (new { rule = new { kind = "MonthlyDay", day = 1, hour = 0, minute = 0, timeZoneId = "UTC", graceSeconds = 3601 }, after = "2026-01-01T00:00:00Z" }, "scheduling.misfire.invalid"),
            (new { rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "UTC" }, after = "2026-01-01T00:00:00Z", count = 11 }, "scheduling.preview.invalid"),
            (new { rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "UTC" }, after = "2026-01-01T00:00:00Z", count = 0 }, "scheduling.preview.invalid"),
            (new { rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "UTC" }, after = "9995-01-01T00:00:00Z" }, "scheduling.preview.invalid"),
            (new { rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "China Standard Time" }, after = "2026-01-01T00:00:00Z" }, "scheduling.timezone.invalid"),
            (new { rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "Unknown/Nowhere" }, after = "2026-01-01T00:00:00Z" }, "scheduling.timezone.invalid"),
            (new { rule = new { kind = "Cron", expression = "0 0 30 2 *", timeZoneId = "UTC" }, after = "2026-01-01T00:00:00Z" }, "scheduling.next_run.unavailable"),
            (new { rule = new { kind = "Cron", expression = "* * * * *", timeZoneId = "UTC", day = 1 }, after = "2026-01-01T00:00:00Z" }, "scheduling.rule.invalid"),
            (new { rule = new { kind = "MonthlyDay", day = 31, hour = 24, minute = 0, timeZoneId = "UTC" }, after = "2026-01-01T00:00:00Z" }, "scheduling.rule.invalid"),
            (new { rule = new { kind = "MonthlyDay", day = 0, hour = 1, minute = 0, timeZoneId = "UTC" }, after = "2026-01-01T00:00:00Z" }, "scheduling.rule.invalid"),
            (new { rule = new { kind = "MonthlyDay", day = 1, hour = 1, minute = 60, timeZoneId = "UTC" }, after = "2026-01-01T00:00:00Z" }, "scheduling.rule.invalid"),
            (new { rule = new { kind = "MonthlyDay", day = 1, minute = 0, timeZoneId = "UTC" }, after = "2026-01-01T00:00:00Z" }, "scheduling.rule.invalid"),
            (new { rule = new { kind = "MonthlyDay", day = 1, hour = 1, minute = 0, timeZoneId = "UTC", expression = "* * * * *" }, after = "2026-01-01T00:00:00Z" }, "scheduling.rule.invalid"),
            (new { rule = new { kind = "Cron", expression = new string('*', 257), timeZoneId = "UTC" }, after = "2026-01-01T00:00:00Z" }, "scheduling.rule.invalid"),
        };
        foreach (var (request, error) in invalid)
        {
            using var response = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/preview", UriKind.Relative), request);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400 for {error}; got {response.StatusCode}.");
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(error, problem.GetProperty("errorCode").GetString());
            _ = problem.GetProperty("timestamp").ReadHttpInt64();
        }
        foreach (var expression in new[] { "* * * *", "0 * * * * * 2026", "@daily", "H * * * *", "60 * * * *", "*/0 * * * *" })
        {
            using var response = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/preview", UriKind.Relative), new
            {
                rule = new { kind = "Cron", expression, timeZoneId = "UTC" },
                after = "2026-01-01T00:00:00Z",
            });
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400 for {expression}; got {response.StatusCode}.");
            Assert.Equal("scheduling.cron.invalid", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        }
        var plans = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        Assert.Empty(plans.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task Preview_ReturnsAvailableSparseDatesInsideTheFiveYearWindow()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        using var response = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/preview", UriKind.Relative), new
        {
            rule = new { kind = "Cron", expression = "0 0 29 2 *", timeZoneId = "UTC" },
            after = "2026-01-01T00:00:00Z",
            count = 10,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadApiDataAsync();
        Assert.Equal("Etc/UTC", preview.GetProperty("rule").GetProperty("timeZoneId").GetString());
        var time = Assert.Single(preview.GetProperty("times").EnumerateArray());
        Assert.Equal("2028-02-29T00:00:00+00:00", time.GetProperty("utc").GetString());
    }

    [Fact]
    public async Task MonthlyDay_ClampsShortMonths_WhileCron31AndLastDayKeepTheirOwnMeaning()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);
        var months = new[]
        {
            (After: "2027-02-01T00:00:00Z", Expected: new[] { "2027-02-28", "2027-02-28", "2027-02-28", "2027-02-28" }),
            (After: "2028-02-01T00:00:00Z", Expected: new[] { "2028-02-28", "2028-02-29", "2028-02-29", "2028-02-29" }),
            (After: "2027-04-01T00:00:00Z", Expected: new[] { "2027-04-28", "2027-04-29", "2027-04-30", "2027-04-30" }),
            (After: "2027-05-01T00:00:00Z", Expected: new[] { "2027-05-28", "2027-05-29", "2027-05-30", "2027-05-31" }),
        };
        foreach (var month in months)
        {
            for (var day = 28; day <= 31; day++)
            {
                using var response = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/preview", UriKind.Relative), new
                {
                    rule = new { kind = "MonthlyDay", day, hour = 9, minute = 0, timeZoneId = "Asia/Shanghai" },
                    after = month.After,
                });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var preview = await response.Content.ReadApiDataAsync();
                Assert.Equal("MonthlyDay", preview.GetProperty("rule").GetProperty("kind").GetString());
                Assert.Equal(day, preview.GetProperty("rule").GetProperty("day").GetInt32());
                var time = Assert.Single(preview.GetProperty("times").EnumerateArray());
                Assert.Equal(month.Expected[day - 28] + "T01:00:00+00:00", time.GetProperty("utc").GetString());
            }
        }

        foreach (var example in new[] { (Cron: "0 9 31 * *", Expected: "2027-03-31T01:00:00+00:00"), (Cron: "0 9 L * *", Expected: "2027-02-28T01:00:00+00:00") })
        {
            using var response = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/preview", UriKind.Relative), new
            {
                rule = new { kind = "Cron", expression = example.Cron, timeZoneId = "Asia/Shanghai" },
                after = "2027-02-01T00:00:00Z",
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var time = Assert.Single((await response.Content.ReadApiDataAsync()).GetProperty("times").EnumerateArray());
            Assert.Equal(example.Expected, time.GetProperty("utc").GetString());
        }
    }

    [Fact]
    public async Task Preview_PreservesTheProductionSecondOffset_WithoutCreatingAPlan()
    {
        await using var app = new PlatformAppWithRootAccount();
        using var client = app.CreateClient();
        await PlatformSettingsAccessTests.LoginAsync(client, PlatformAppWithRootAccount.RootUserName, PlatformAppWithRootAccount.RootPassword);

        using var response = await client.PostAsJsonAsync(new Uri("/api/scheduling/tasks/preview", UriKind.Relative), new
        {
            rule = new { kind = "Cron", expression = "  10  * 7-23 * * ?  ", timeZoneId = "Asia/Shanghai" },
            after = "2026-10-02T00:00:00Z",
            count = 2,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadApiDataAsync();
        var rule = preview.GetProperty("rule");
        Assert.Equal("Cron", rule.GetProperty("kind").GetString());
        Assert.Equal("10 * 7-23 * * ?", rule.GetProperty("expression").GetString());
        Assert.Equal(6, rule.GetProperty("cronFieldCount").GetInt32());
        Assert.Equal("Asia/Shanghai", rule.GetProperty("timeZoneId").GetString());
        var times = preview.GetProperty("times").EnumerateArray().ToArray();
        Assert.Equal(2, times.Length);
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T00:00:10Z", System.Globalization.CultureInfo.InvariantCulture), times[0].GetProperty("utc").GetDateTimeOffset());
        Assert.Equal("2026-10-02T08:00:10+08:00", times[0].GetProperty("local").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T00:01:10Z", System.Globalization.CultureInfo.InvariantCulture), times[1].GetProperty("utc").GetDateTimeOffset());
        var plans = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/scheduling/tasks/", UriKind.Relative));
        Assert.Empty(plans.GetProperty("data").EnumerateArray());
    }
}
