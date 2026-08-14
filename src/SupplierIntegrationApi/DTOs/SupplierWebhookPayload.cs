namespace SupplierIntegrationApi.DTOs;

public sealed record SupplierWebhookPayload(
    string? EventType,
    string? ProductId,
    int? StockQuantity,
    decimal? Price,
    string? Name,
    bool? IsActive);

public sealed record SupplierWebhookResponse(string EventId, string Outcome, bool Duplicate);
