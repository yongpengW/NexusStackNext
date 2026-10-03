namespace NexusStackNext.Platform.Infrastructure.Persistence;

// Policy and usage belong to the database, so hosts cannot race to replace each other's limits.
internal sealed class SettingFactCapacity
{
    public int Id { get; set; }
    public long RetainedRecords { get; set; }
    public long RetainedPayloadBytes { get; set; }
    public long MaxRecords { get; set; }
    public long MaxPayloadBytes { get; set; }
    public int MaxRecordPayloadBytes { get; set; }
}
