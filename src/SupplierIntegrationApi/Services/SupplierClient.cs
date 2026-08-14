using System.Net;
using System.Net.Http.Json;
using Polly.Timeout;
using SupplierIntegrationApi.DTOs;
using SupplierIntegrationApi.Interfaces;

namespace SupplierIntegrationApi.Services;

public sealed class SupplierClient(HttpClient httpClient, ILogger<SupplierClient> logger) : ISupplierClient
{
    public async Task<SupplierProductsPageDto> GetProductsPageAsync(
        int page, int pageSize, CancellationToken cancellationToken)
    {
        logger.LogInformation("Requesting supplier products page {Page}", page);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync($"products?page={page}&pageSize={pageSize}", cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SupplierException("supplier_timeout", "The supplier request timed out.");
        }
        catch (TimeoutRejectedException exception)
        {
            throw new SupplierException("supplier_timeout", "The supplier request timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new SupplierException("supplier_unavailable", "The supplier is unavailable.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw response.StatusCode switch
                {
                    HttpStatusCode.RequestTimeout =>
                        new SupplierException("supplier_timeout", "The supplier request timed out."),
                    HttpStatusCode.TooManyRequests =>
                        new SupplierException("supplier_rate_limited", "The supplier rate limit was exceeded."),
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        new SupplierException("supplier_unauthorized", "The supplier rejected its configured credentials."),
                    >= HttpStatusCode.InternalServerError =>
                        new SupplierException("supplier_unavailable", "The supplier is unavailable."),
                    _ => new SupplierException("supplier_rejected", "The supplier rejected the request.")
                };
            }

            try
            {
                var result = await response.Content.ReadFromJsonAsync<SupplierProductsPageDto>(cancellationToken);
                return result ?? throw new SupplierException("supplier_invalid_response", "The supplier returned an invalid response.");
            }
            catch (SupplierException) { throw; }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
            {
                throw new SupplierException("supplier_invalid_response", "The supplier returned an invalid response.", exception);
            }
        }
    }
}
