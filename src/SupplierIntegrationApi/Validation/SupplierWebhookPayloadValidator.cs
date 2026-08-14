using FluentValidation;
using SupplierIntegrationApi.DTOs;

namespace SupplierIntegrationApi.Validation;

public sealed class SupplierWebhookPayloadValidator : AbstractValidator<SupplierWebhookPayload>
{
    private static readonly string[] SupportedEventTypes =
        ["inventory.updated", "price.updated", "product.updated"];

    public SupplierWebhookPayloadValidator()
    {
        RuleFor(payload => payload.EventType).NotEmpty().MaximumLength(128);

        When(payload => SupportedEventTypes.Contains(payload.EventType, StringComparer.Ordinal), () =>
        {
            RuleFor(payload => payload.ProductId).NotEmpty().MaximumLength(128);
        });

        When(payload => payload.EventType == "inventory.updated", () =>
        {
            RuleFor(payload => payload.StockQuantity).NotNull().GreaterThanOrEqualTo(0);
        });

        When(payload => payload.EventType == "price.updated", () =>
        {
            RuleFor(payload => payload.Price).NotNull().GreaterThan(0);
        });

        When(payload => payload.EventType == "product.updated", () =>
        {
            RuleFor(payload => payload)
                .Must(payload => payload.Name is not null || payload.Price.HasValue ||
                    payload.StockQuantity.HasValue || payload.IsActive.HasValue)
                .WithMessage("At least one mutable product field is required.");
            When(payload => payload.Name is not null, () =>
                RuleFor(payload => payload.Name).NotEmpty().MaximumLength(256));
            When(payload => payload.Price.HasValue, () =>
                RuleFor(payload => payload.Price).GreaterThan(0));
            When(payload => payload.StockQuantity.HasValue, () =>
                RuleFor(payload => payload.StockQuantity).GreaterThanOrEqualTo(0));
        });
    }
}
