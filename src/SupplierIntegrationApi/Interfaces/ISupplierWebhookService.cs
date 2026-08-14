using SupplierIntegrationApi.DTOs;

namespace SupplierIntegrationApi.Interfaces;

public interface ISupplierWebhookService
{
    Task<SupplierWebhookResponse> ProcessAsync(
        string externalEventId,
        SupplierWebhookPayload payload,
        CancellationToken cancellationToken);
}
