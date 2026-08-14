using SupplierIntegrationApi.Enums;

namespace SupplierIntegrationApi.DTOs;

public sealed record ProductResponse(int Id, string ExternalId, string Sku, string Name, decimal Price,
    int StockQuantity, bool IsActive, DateTime LastSyncedAtUtc, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public sealed record SyncRunResponse(long Id, SyncTriggerType TriggerType, SyncRunStatus Status,
    DateTime StartedAtUtc, DateTime? CompletedAtUtc, int ItemsRead, int ItemsCreated, int ItemsUpdated,
    int ItemsUnchanged, string? FailureCode, string? FailureMessage);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalCount);
