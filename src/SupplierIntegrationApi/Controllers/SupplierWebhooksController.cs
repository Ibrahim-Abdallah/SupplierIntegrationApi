using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupplierIntegrationApi.DTOs;
using SupplierIntegrationApi.Interfaces;

namespace SupplierIntegrationApi.Controllers;

[ApiController]
[Route("api/webhooks/supplier")]
[AllowAnonymous]
public sealed class SupplierWebhooksController(
    IWebhookSignatureVerifier signatureVerifier,
    IValidator<SupplierWebhookPayload> validator,
    ISupplierWebhookService webhookService,
    ILogger<SupplierWebhooksController> logger) : ControllerBase
{
    public const int MaximumBodyBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<SupplierWebhookResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<SupplierWebhookResponse>> Receive(
        [FromHeader(Name = "X-Supplier-Event-Id")] string? suppliedEventId,
        [FromHeader(Name = "X-Supplier-Signature")] string? suppliedSignature,
        CancellationToken cancellationToken)
    {
        var eventIdValues = Request.Headers["X-Supplier-Event-Id"];
        var eventId = eventIdValues.Count == 1 ? suppliedEventId?.Trim() ?? string.Empty : string.Empty;
        if (eventId.Length is 0 or > 128)
            return Problem(title: "Invalid supplier event ID", statusCode: StatusCodes.Status400BadRequest);

        if (Request.ContentLength > MaximumBodyBytes)
            return Problem(title: "Webhook payload is too large", statusCode: StatusCodes.Status413PayloadTooLarge);

        var body = await ReadBoundedBodyAsync(cancellationToken);
        if (body is null)
            return Problem(title: "Webhook payload is too large", statusCode: StatusCodes.Status413PayloadTooLarge);

        var signatureValues = Request.Headers["X-Supplier-Signature"];
        var signature = signatureValues.Count == 1 ? suppliedSignature : null;
        if (!signatureVerifier.IsValid(body, signature))
        {
            logger.LogWarning("Supplier webhook {ExternalEventId} rejected due to invalid signature", eventId);
            return Problem(title: "Invalid webhook signature", statusCode: StatusCodes.Status401Unauthorized);
        }

        SupplierWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SupplierWebhookPayload>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return Problem(title: "Malformed webhook payload", statusCode: StatusCodes.Status400BadRequest);
        }

        if (payload is null)
            return Problem(title: "Malformed webhook payload", statusCode: StatusCodes.Status400BadRequest);

        var validation = await validator.ValidateAsync(payload, cancellationToken);
        if (!validation.IsValid)
        {
            var errors = validation.Errors.GroupBy(error => error.PropertyName)
                .ToDictionary(group => group.Key, group => group.Select(error => error.ErrorMessage).ToArray());
            return ValidationProblem(new ValidationProblemDetails(errors)
            {
                Title = "Invalid webhook payload",
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Ok(await webhookService.ProcessAsync(eventId, payload, cancellationToken));
    }

    private async Task<byte[]?> ReadBoundedBodyAsync(CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await Request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > MaximumBodyBytes) return null;
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }
}
