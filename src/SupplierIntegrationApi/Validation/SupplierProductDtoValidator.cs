using FluentValidation;
using SupplierIntegrationApi.DTOs;

namespace SupplierIntegrationApi.Validation;

public sealed class SupplierProductDtoValidator : AbstractValidator<SupplierProductDto>
{
    public SupplierProductDtoValidator()
    {
        RuleFor(product => product.Id).NotEmpty().MaximumLength(128);
        RuleFor(product => product.Sku).NotEmpty().MaximumLength(128);
        RuleFor(product => product.Name).NotEmpty().MaximumLength(256);
        RuleFor(product => product.Price).GreaterThanOrEqualTo(0);
        RuleFor(product => product.StockQuantity).GreaterThanOrEqualTo(0);
    }
}
