using SupplierIntegrationApi.DTOs;

namespace SupplierIntegrationApi.Interfaces;

public interface ISupplierClient
{
    Task<SupplierProductsPageDto> GetProductsPageAsync(int page, int pageSize, CancellationToken cancellationToken);
}
