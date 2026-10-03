namespace NexusStackNext.Identity.Infrastructure.Persistence;

internal sealed class IdentityFactCapacity
{
    public int Id { get; set; }
    public long RetainedRecords { get; set; }
    public long RetainedPayloadBytes { get; set; }
    public long MaxRecords { get; set; }
    public long MaxPayloadBytes { get; set; }
    public int MaxRecordPayloadBytes { get; set; }
}
