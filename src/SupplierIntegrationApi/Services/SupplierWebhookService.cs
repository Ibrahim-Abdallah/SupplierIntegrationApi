using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SupplierIntegrationApi.Data;
using SupplierIntegrationApi.DTOs;
using SupplierIntegrationApi.Entities;
using SupplierIntegrationApi.Enums;
using SupplierIntegrationApi.Interfaces;

namespace SupplierIntegrationApi.Services;

public sealed class SupplierWebhookService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<SupplierWebhookService> logger) : ISupplierWebhookService
{
    public async Task<SupplierWebhookResponse> ProcessAsync(
        string externalEventId,
        SupplierWebhookPayload payload,
        CancellationToken cancellationToken)
    {
        if (await dbContext.WebhookEvents.AsNoTracking()
            .AnyAsync(item => item.ExternalEventId == externalEventId, cancellationToken))
        {
            logger.LogInformation("Duplicate supplier webhook {ExternalEventId} detected", externalEventId);
            return new SupplierWebhookResponse(externalEventId, "duplicate", true);
        }

        return await ProcessClaimAsync(externalEventId, payload, cancellationToken);
    }

    private async Task<SupplierWebhookResponse> ProcessClaimAsync(
        string externalEventId,
        SupplierWebhookPayload payload,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var webhookEvent = new WebhookEvent
        {
            ExternalEventId = externalEventId,
            EventType = payload.EventType!,
            Status = WebhookEventStatus.Received,
            ReceivedAtUtc = now
        };

        dbContext.WebhookEvents.Add(webhookEvent);
        try
        {
            // This insert is the authoritative claim. Product state is untouched until it succeeds.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsExternalEventIdDuplicate(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogInformation("Duplicate supplier webhook {ExternalEventId} detected", externalEventId);
            return new SupplierWebhookResponse(externalEventId, "duplicate", true);
        }

        var outcome = await ApplyAsync(webhookEvent, payload, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Supplier webhook {ExternalEventId} completed with {Outcome}", externalEventId, outcome);
        return new SupplierWebhookResponse(externalEventId, outcome, false);
    }

    private async Task<string> ApplyAsync(
        WebhookEvent webhookEvent,
        SupplierWebhookPayload payload,
        DateTime now,
        CancellationToken cancellationToken)
    {
        webhookEvent.ProcessedAtUtc = now;
        webhookEvent.ProductExternalId = payload.ProductId;

        if (payload.EventType is not ("inventory.updated" or "price.updated" or "product.updated"))
        {
            webhookEvent.Status = WebhookEventStatus.Ignored;
            webhookEvent.FailureCode = "unsupported_event_type";
            logger.LogInformation("Unsupported supplier webhook event {EventType} ignored", payload.EventType);
            return "ignored";
        }

        var product = await dbContext.Products.SingleOrDefaultAsync(
            item => item.ExternalId == payload.ProductId, cancellationToken);
        if (product is null)
        {
            webhookEvent.Status = WebhookEventStatus.Ignored;
            webhookEvent.FailureCode = "unknown_product";
            logger.LogInformation("Supplier webhook for unknown product {ProductExternalId} ignored", payload.ProductId);
            return "ignored";
        }

        switch (payload.EventType)
        {
            case "inventory.updated":
                product.StockQuantity = payload.StockQuantity!.Value;
                break;
            case "price.updated":
                product.Price = payload.Price!.Value;
                break;
            case "product.updated":
                if (payload.Name is not null) product.Name = payload.Name;
                if (payload.Price.HasValue) product.Price = payload.Price.Value;
                if (payload.StockQuantity.HasValue) product.StockQuantity = payload.StockQuantity.Value;
                if (payload.IsActive.HasValue) product.IsActive = payload.IsActive.Value;
                break;
        }

        product.UpdatedAtUtc = now;
        webhookEvent.Status = WebhookEventStatus.Processed;
        webhookEvent.FailureCode = null;
        return "processed";
    }

    private static bool IsExternalEventIdDuplicate(DbUpdateException exception)
    {
        if (exception.InnerException is not DbException databaseException) return false;
        var message = databaseException.Message;

        if (databaseException.GetType().FullName == "Microsoft.Data.SqlClient.SqlException")
        {
            var number = databaseException.GetType().GetProperty("Number")?.GetValue(databaseException) as int?;
            return number is 2601 or 2627 &&
                message.Contains("IX_WebhookEvents_ExternalEventId", StringComparison.OrdinalIgnoreCase);
        }

        return databaseException.GetType().FullName == "Microsoft.Data.Sqlite.SqliteException" &&
            GetSqliteErrorCode(databaseException) == 19 &&
            message.Contains("WebhookEvents.ExternalEventId", StringComparison.OrdinalIgnoreCase);
    }

    private static int? GetSqliteErrorCode(DbException exception) =>
        exception.GetType().GetProperty("SqliteErrorCode")?.GetValue(exception) as int?;
}
