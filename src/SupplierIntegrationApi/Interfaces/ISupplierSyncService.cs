using SupplierIntegrationApi.DTOs;

namespace SupplierIntegrationApi.Interfaces;

public interface ISupplierSyncService
{
    Task<SyncRunResponse> RunManualAsync(CancellationToken cancellationToken);
}
