using SupplierIntegrationApi.Enums;

namespace SupplierIntegrationApi.Entities;

public class WebhookEvent
{
    public long Id { get; set; }
    public string ExternalEventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public WebhookEventStatus Status { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public string? ProductExternalId { get; set; }
    public string? FailureCode { get; set; }
}
