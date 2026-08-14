using SupplierIntegrationApi.Enums;

namespace SupplierIntegrationApi.Entities;

public class SyncRun
{
    public long Id { get; set; }
    public SyncTriggerType TriggerType { get; set; }
    public SyncRunStatus Status { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int ItemsRead { get; set; }
    public int ItemsCreated { get; set; }
    public int ItemsUpdated { get; set; }
    public int ItemsUnchanged { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
}
