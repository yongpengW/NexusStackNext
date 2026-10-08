using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NexusStackNext.CostingHost;
using NexusStackNext.IntegrationSupport;

namespace NexusStackNext.Costing.IntegrationTests;

public sealed class CostingBatchHttpTests(CostingDatabaseFixture database) : IClassFixture<CostingDatabaseFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task MixedFieldErrors_PreserveEveryOriginalRowAndCountAllFieldsWithoutInventingArrayErrors()
    {
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
        host.Authenticate();
        var batchId = Guid.NewGuid();
        using var rejected = await host.Client.PostAsJsonAsync(new Uri("/api/costing/batches", UriKind.Relative), new
        {
            batchRequestId = batchId,
            rows = new[]
            {
                new { sourceRow = "27", itemId = "invalid-guid", expectedVersion = "-1", purchaseCost = -1m, freightCost = "invalid-decimal" },
                new { sourceRow = "28", itemId = Guid.NewGuid().ToString(), expectedVersion = "0", purchaseCost = -1m, freightCost = "20" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var report = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, report.GetProperty("errorCount").GetInt32());
        var errors = report.GetProperty("errors").EnumerateArray().ToArray();
        Assert.Equal(4, errors.Count(x => x.GetProperty("sourceRow").GetInt32() == 27));
        Assert.Equal(new[] { "expectedVersion", "freightCost", "itemId", "purchaseCost" },
            errors.Where(x => x.GetProperty("sourceRow").GetInt32() == 27).Select(x => x.GetProperty("field").GetString()).Order());
        using var onlyGuid = await host.Client.PostAsJsonAsync(new Uri("/api/costing/batches", UriKind.Relative), new
        {
            batchRequestId = batchId,
            rows = new[] { new { sourceRow = 88, itemId = "invalid-guid", expectedVersion = "0", purchaseCost = 80m, freightCost = 20m } },
        });
        var single = await onlyGuid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, single.GetProperty("errorCount").GetInt32());
        Assert.Equal("itemId", Assert.Single(single.GetProperty("errors").EnumerateArray()).GetProperty("field").GetString());
        using var missing = await host.Client.GetAsync(new Uri($"/api/costing/batches/{batchId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [PostgresFact]
    public async Task ConcurrentEquivalentRequests_ShareFirstAcceptanceAndChildIdentity_WhileChangedRawContentConflicts()
    {
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
        host.Authenticate();
        // Keep identities containing the original amount digits to catch accidental text replacement.
        var batchId = Guid.Parse("c83b3c60-630d-4a5f-a126-fb58c6000080");
        var item = Guid.Parse("b675b6fb-a580-4e14-9f10-2d6622860080");
        var canonical = $$"""{"batchRequestId":"{{batchId}}","rows":[{"itemId":"{{item}}","expectedVersion":"0","purchaseCost":80,"freightCost":20}]}""";
        var equivalent = $$"""{"rows":[{"freightCost":20.0000,"purchaseCost":8e1,"expectedVersion":0,"itemId":"{{item}}"}],"batchRequestId":"{{batchId}}"}""";
        using var firstBody = new StringContent(canonical, Encoding.UTF8, "application/json");
        using var secondBody = new StringContent(equivalent, Encoding.UTF8, "application/json");
        var replies = await Task.WhenAll(host.Client.PostAsync(new Uri("/api/costing/batches", UriKind.Relative), firstBody),
            host.Client.PostAsync(new Uri("/api/costing/batches", UriKind.Relative), secondBody));
        using var first = replies[0];
        using var second = replies[1];
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetRawText(),
            (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetRawText());
        var rows = (await host.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/costing/batches/{batchId}/rows", UriKind.Relative))).GetProperty("data");
        Assert.NotEqual(Guid.Empty, Assert.Single(rows.EnumerateArray()).GetProperty("taskId").GetGuid());
        using var changed = await host.Client.PostAsJsonAsync(new Uri("/api/costing/batches", UriKind.Relative), new
        {
            batchRequestId = batchId,
            rows = new[] { new { itemId = item, expectedVersion = "0", purchaseCost = 81m, freightCost = 20m } },
        });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
    }

    [PostgresFact]
    public async Task RowLimit_AcceptsFiveThousandImmutableRows_AndRejectsTheNextRowBeforeRegisteringWork()
    {
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
        host.Authenticate();
        using var client = new HttpClient { BaseAddress = host.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = host.Client.DefaultRequestHeaders.Authorization;
        var rows = Enumerable.Range(1, 5001).Select(sourceRow => new
        {
            sourceRow,
            itemId = Guid.NewGuid(),
            expectedVersion = "0",
            purchaseCost = 80m,
            freightCost = 20m,
        }).ToArray();
        var acceptedId = Guid.NewGuid();
        var input = new { batchRequestId = acceptedId, rows = rows.Take(5000).ToArray() };
        var concurrent = await Task.WhenAll(client.PostAsJsonAsync(new Uri("/api/costing/batches", UriKind.Relative), input),
            client.PostAsJsonAsync(new Uri("/api/costing/batches", UriKind.Relative), input));
        using var accepted = concurrent[0];
        using var replay = concurrent[1];
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(5000, (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("pending").GetInt32());
        using var listed = await host.Client.GetAsync(new Uri($"/api/costing/batches/{acceptedId}/rows", UriKind.Relative));
        var page = await listed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("5000", page.GetProperty("total").GetString());
        Assert.Equal(50, page.GetProperty("data").GetArrayLength());
        var rejectedId = Guid.NewGuid();
        using var rejected = await host.Client.PostAsJsonAsync(new Uri("/api/costing/batches", UriKind.Relative), new { batchRequestId = rejectedId, rows });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var missing = await host.Client.GetAsync(new Uri($"/api/costing/batches/{rejectedId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [PostgresFact]
    public async Task RequestByteLimit_UsesActualChunkedBytesBeforeJsonParsing()
    {
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
        host.Authenticate();
        var batchId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new { batchRequestId = batchId, rows = new[] { new { itemId = Guid.NewGuid(), expectedVersion = "0", purchaseCost = 80m, freightCost = 20m } } });
        var bytes = Encoding.UTF8.GetBytes(new string(' ', 2 * 1024 * 1024) + json);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/costing/batches", UriKind.Relative));
        request.Headers.TransferEncodingChunked = true;
        request.Content = new StreamContent(new MemoryStream(bytes));
        request.Content.Headers.ContentType = new("application/json");
        using var rejected = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        using var missing = await host.Client.GetAsync(new Uri($"/api/costing/batches/{batchId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [PostgresFact]
    public async Task MalformedRowTypes_AreClientErrorsWithNoAcceptedWork()
    {
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
        host.Authenticate();
        foreach (var sourceRow in new[] { "\"oops\"", "null", "{}", "[]" })
        {
            var batchId = Guid.NewGuid();
            var json = $$"""{"batchRequestId":"{{batchId}}","rows":[{"sourceRow":{{sourceRow}},"itemId":"{{Guid.NewGuid()}}","expectedVersion":"0","purchaseCost":80,"freightCost":20}]}""";
            using var body = new StringContent(json, Encoding.UTF8, "application/json");
            using var rejected = await host.Client.PostAsync(new Uri("/api/costing/batches", UriKind.Relative), body);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
    }

    [PostgresFact]
    public async Task BatchList_IsBoundedFiltersStateAndDoesNotReturnInputSnapshots()
    {
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
        host.Authenticate();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        foreach (var batchRequestId in new[] { first, second })
        {
            using var accepted = await host.Client.PostAsJsonAsync(new Uri("/api/costing/batches", UriKind.Relative),
                new { batchRequestId, rows = new[] { new { itemId = Guid.NewGuid(), expectedVersion = "0", purchaseCost = 80m, freightCost = 20m } } });
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }
        using var cancelled = await host.Client.PostAsJsonAsync(new Uri($"/api/costing/batches/{first}/cancel", UriKind.Relative), new { expectedEpoch = "0" });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        using var listed = await host.Client.GetAsync(new Uri("/api/costing/batches?limit=1&state=Pending", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var page = await listed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1", page.GetProperty("total").GetString());
        var pending = Assert.Single(page.GetProperty("data").EnumerateArray());
        Assert.Equal(second, pending.GetProperty("batchId").GetGuid());
        Assert.False(pending.TryGetProperty("rows", out _));
        Assert.False(pending.TryGetProperty("contentHash", out _));
        foreach (var query in new[] { "limit=201", "page=2147483647", "state=Unknown" })
        {
            using var invalid = await host.Client.GetAsync(new Uri("/api/costing/batches?" + query, UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }

    [PostgresFact]
    public async Task JsonSchema_RejectsUnknownDuplicateAndMissingFieldsWithoutAcceptingWork()
    {
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
        host.Authenticate();
        foreach (var extra in new[] { ",\"secret\":\"untrusted\"", ",\"purchaseCost\":90", ",\"ExpectedVersion\":\"1\"" })
        {
            var batchId = Guid.NewGuid();
            var json = $$"""{"batchRequestId":"{{batchId}}","rows":[{"sourceRow":1,"itemId":"{{Guid.NewGuid()}}","expectedVersion":"0","purchaseCost":80,"freightCost":20{{extra}}}]}""";
            using var body = new StringContent(json, Encoding.UTF8, "application/json");
            using var rejected = await host.Client.PostAsync(new Uri("/api/costing/batches", UriKind.Relative), body);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var missing = await host.Client.GetAsync(new Uri($"/api/costing/batches/{batchId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        using var incomplete = await host.Client.PostAsJsonAsync(new Uri("/api/costing/batches", UriKind.Relative),
            new { batchRequestId = Guid.NewGuid(), rows = new[] { new { itemId = Guid.NewGuid(), expectedVersion = "0", purchaseCost = 80m } } });
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
    }

    [PostgresFact]
    public async Task Validation_RejectsAllInvalidOriginalRowsBeforeDeduplication_AndBoundsReportedErrors()
    {
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
        host.Authenticate();
        var batchId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var rows = Enumerable.Range(1, 25).Select(sourceRow => new
        {
            sourceRow,
            itemId,
            expectedVersion = "0",
            purchaseCost = sourceRow == 25 ? 80m : -1m,
            freightCost = 20m,
        }).ToArray();
        using var rejected = await host.Client.PostAsJsonAsync(new Uri("/api/costing/batches", UriKind.Relative), new { batchRequestId = batchId, rows });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var errors = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(24, errors.GetProperty("errorCount").GetInt32());
        var shown = errors.GetProperty("errors").EnumerateArray().ToArray();
        Assert.Equal(20, shown.Length);
        Assert.Equal(1, shown[0].GetProperty("sourceRow").GetInt32());
        Assert.Equal("purchaseCost", shown[0].GetProperty("field").GetString());
        using var missing = await host.Client.GetAsync(new Uri($"/api/costing/batches/{batchId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [PostgresFact]
    public async Task Worker_CommitsWinningRowsAndBusinessRejections_ThenCalculatesAcceptedCosts()
    {
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString, worker: true);
        host.Authenticate();
        var batchId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var rejectedItem = Guid.NewGuid();
        using var accepted = await host.Client.PostAsJsonAsync(new Uri("/api/costing/batches", UriKind.Relative), new
        {
            batchRequestId = batchId,
            rows = new[]
            {
                new { sourceRow = 1, itemId, expectedVersion = "0", purchaseCost = 1m, freightCost = 2m },
                new { sourceRow = 2, itemId = rejectedItem, expectedVersion = "1", purchaseCost = 10m, freightCost = 20m },
                new { sourceRow = 3, itemId, expectedVersion = "0", purchaseCost = 80m, freightCost = 20m },
            },
        });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var status = default(JsonElement);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            status = (await host.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/costing/batches/{batchId}", UriKind.Relative))).GetProperty("data");
            if (status.GetProperty("state").GetString() == "CompletedWithErrors") { break; }
            await Task.Delay(100);
        }
        Assert.Equal("CompletedWithErrors", status.GetProperty("state").GetString());
        Assert.Equal(3, status.GetProperty("checkpoint").GetInt32());
        Assert.Equal(1, status.GetProperty("imported").GetInt32());
        Assert.Equal(1, status.GetProperty("rejected").GetInt32());
        Assert.Equal(1, status.GetProperty("duplicateSuperseded").GetInt32());
        var rows = (await host.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/costing/batches/{batchId}/rows", UriKind.Relative)))
            .GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(new[] { "DuplicateSuperseded", "Rejected", "Imported" }, rows.Select(x => x.GetProperty("outcome").GetString()));
        Assert.Equal("costing.version_conflict", rows[1].GetProperty("errorCode").GetString());
        using var absent = await host.Client.GetAsync(new Uri($"/api/costing/items/{rejectedItem}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        var taskId = rows[2].GetProperty("taskId").GetGuid();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var task = (await host.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/costing/tasks/{taskId}", UriKind.Relative))).GetProperty("data");
            if (task.GetProperty("state").GetString() == "Succeeded") { break; }
            await Task.Delay(100);
        }
        var sheet = (await host.Client.GetFromJsonAsync<JsonElement>(new Uri($"/api/costing/items/{itemId}", UriKind.Relative))).GetProperty("data");
        Assert.Equal(100m, sheet.GetProperty("unitCost").GetDecimal());
    }

    [PostgresFact]
    public async Task EmptyBatch_IsRejectedWithoutRegisteringWork()
    {
        await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
        host.Authenticate();
        var requestId = Guid.NewGuid();
        using var response = await host.Client.PostAsJsonAsync(new Uri("/api/costing/batches", UriKind.Relative),
            new { batchRequestId = requestId, rows = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var missing = await host.Client.GetAsync(new Uri($"/api/costing/batches/{requestId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [PostgresFact]
    public async Task Acceptance_FreezesEveryOriginalRow_AndReplayAfterRestartPreservesIdentity()
    {
        var batchId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var request = new
        {
            batchRequestId = batchId,
            rows = new[]
            {
                new { sourceRow = 10, itemId, expectedVersion = "0", purchaseCost = 80m, freightCost = 20m },
                new { sourceRow = 20, itemId, expectedVersion = "0", purchaseCost = 90m, freightCost = 10m },
            },
        };
        DateTimeOffset? firstAcceptedAt = null;
        Guid? childTaskId = null;
        for (var cycle = 0; cycle < 2; cycle++)
        {
            await using var host = await BusinessProcess.StartAsync(typeof(CostingHostMarker).Assembly.Location, "Costing", database.ConnectionString);
            host.Authenticate();
            using var accepted = await host.Client.PostAsJsonAsync(new Uri("/api/costing/batches", UriKind.Relative), request);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var status = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("Pending", status.GetProperty("state").GetString());
            firstAcceptedAt ??= status.GetProperty("createdAt").GetDateTimeOffset();
            Assert.Equal(firstAcceptedAt, status.GetProperty("createdAt").GetDateTimeOffset());
            Assert.Equal(2, status.GetProperty("totalRows").GetInt32());
            using var listed = await host.Client.GetAsync(new Uri($"/api/costing/batches/{batchId}/rows?limit=1&page=1", UriKind.Relative));
            var firstRow = Assert.Single((await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray());
            Assert.Equal(10, firstRow.GetProperty("sourceRow").GetInt32());
            Assert.Equal("DuplicateSuperseded", firstRow.GetProperty("outcome").GetString());
            Assert.Equal(2, firstRow.GetProperty("effectiveSequence").GetInt32());
            using var last = await host.Client.GetAsync(new Uri($"/api/costing/batches/{batchId}/rows?limit=1&page=2", UriKind.Relative));
            var lastRow = Assert.Single((await last.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray());
            childTaskId ??= lastRow.GetProperty("taskId").GetGuid();
            Assert.Equal(childTaskId, lastRow.GetProperty("taskId").GetGuid());
            using var unchangedBusiness = await host.Client.GetAsync(new Uri($"/api/costing/items/{itemId}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, unchangedBusiness.StatusCode);
            using var changed = await host.Client.PostAsJsonAsync(new Uri("/api/costing/batches", UriKind.Relative),
                request with { rows = request.rows.Reverse().ToArray() });
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        }
    }
}
