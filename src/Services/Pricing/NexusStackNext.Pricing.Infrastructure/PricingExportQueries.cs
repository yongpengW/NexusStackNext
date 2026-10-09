using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Application.Messaging;
using NexusStackNext.BuildingBlocks.Application.Security;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.Pricing.Application;
using Npgsql;
using NpgsqlTypes;

namespace NexusStackNext.Pricing.Infrastructure;

internal sealed class PricingExportQueries(PricingDbContext database, ICurrentUser user) : IQueryHandler<ListPricingExports, PricingExportPage>
{
    public async Task<Result<PricingExportPage>> HandleAsync(ListPricingExports query, CancellationToken cancellationToken = default)
    {
        var owner = user.UserId;
        if (string.IsNullOrWhiteSpace(owner)) { return Result.Failure<PricingExportPage>(new Error("pricing.export.unauthenticated", "需要当前有效身份。")); }
        if (query.Limit is < 1 or > 200 || query.State is not (null or "Queued" or "Generating" or "Publishing" or "Succeeded" or "Canceled" or "Failed")
            || query.AcceptedFrom > query.AcceptedThrough) { return Invalid(); }
        var fingerprint = Fingerprint(owner, query);
        ExportCursor? cursor = null;
        if (query.Cursor is not null && !TryDecode(query.Cursor, fingerprint, out cursor)) { return Invalid(); }
        // PostgreSQL timestamp 为微秒：下界向上、上界向下，不能截断后把边界外的行纳入。
        if (query.AcceptedFrom is { } last && (last.UtcTicks + 9) / 10 * 10 > DateTimeOffset.MaxValue.UtcTicks)
        { return Result.Success(new PricingExportPage([], null)); }
        DateTimeOffset? from = query.AcceptedFrom is { } lower ? new DateTimeOffset((lower.UtcTicks + 9) / 10 * 10, TimeSpan.Zero) : null;
        DateTimeOffset? through = query.AcceptedThrough is { } upper ? new DateTimeOffset(upper.UtcTicks / 10 * 10, TimeSpan.Zero) : null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var rows = await database.Database.SqlQueryRaw<PricingExportMetadata>(PricingExportReadModel.Select + """

                WHERE e."OwnerId" = @owner AND (@state IS NULL OR e."State" = @state)
                AND (@from IS NULL OR e."AcceptedAt" >= @from) AND (@through IS NULL OR e."AcceptedAt" <= @through)
                AND (@cursorAt IS NULL OR e."AcceptedAt" < @cursorAt OR (e."AcceptedAt" = @cursorAt AND e."Id" < @cursorId))
                ORDER BY e."AcceptedAt" DESC, e."Id" DESC LIMIT @limit
                """, new NpgsqlParameter("owner", owner), PricingExportReadModel.Parameter("state", NpgsqlDbType.Text, query.State),
                PricingExportReadModel.Parameter("from", NpgsqlDbType.TimestampTz, from),
                PricingExportReadModel.Parameter("through", NpgsqlDbType.TimestampTz, through),
                PricingExportReadModel.Parameter("cursorAt", NpgsqlDbType.TimestampTz, cursor?.AcceptedAt),
                PricingExportReadModel.Parameter("cursorId", NpgsqlDbType.Uuid, cursor?.Id), new NpgsqlParameter("limit", query.Limit + 1))
                .ToArrayAsync(budget.Token).ConfigureAwait(false);
            var items = rows.Take(query.Limit).Select(static row => row.Status()).ToArray();
            var next = rows.Length > query.Limit ? Encode(items[^1], fingerprint) : null;
            return Result.Success(new PricingExportPage(items, next));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (NpgsqlException) { return Unavailable(); }
    }

    private static string Fingerprint(string owner, ListPricingExports query) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('|', owner, query.State ?? "Any", query.AcceptedFrom?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "-",
            query.AcceptedThrough?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "-"))));
    private static string Encode(PricingExportStatus status, string fingerprint) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        string.Join('|', "1", status.AcceptedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), status.ExportId.ToString("D"), fingerprint)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool TryDecode(string value, string fingerprint, out ExportCursor? cursor)
    {
        cursor = null;
        if (value.Length is < 1 or > 256 || value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))) { return false; }
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='))).Split('|');
            if (parts.Length != 4 || parts[0] != "1" || parts[3] != fingerprint
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks || ticks % 10 != 0
                || !Guid.TryParseExact(parts[2], "D", out var id) || id == Guid.Empty) { return false; }
            cursor = new(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static Result<PricingExportPage> Invalid() => Result.Failure<PricingExportPage>(new Error("pricing.export.invalid_query", "列表筛选或游标无效。"));
    private static Result<PricingExportPage> Unavailable() => Result.Failure<PricingExportPage>(new Error("pricing.export.unavailable", "本人列表暂不可用。"));
    private sealed record ExportCursor(DateTimeOffset AcceptedAt, Guid Id);
}
