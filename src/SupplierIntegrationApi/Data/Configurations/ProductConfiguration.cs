using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupplierIntegrationApi.Entities;

namespace SupplierIntegrationApi.Data.Configurations;

public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", table =>
            table.HasCheckConstraint("CK_Products_StockQuantity_NonNegative", "[StockQuantity] >= 0"));
        builder.HasKey(product => product.Id);
        builder.Property(product => product.ExternalId).HasMaxLength(128).IsRequired();
        builder.Property(product => product.Sku).HasMaxLength(128).IsRequired();
        builder.Property(product => product.Name).HasMaxLength(256).IsRequired();
        builder.Property(product => product.Price).HasPrecision(18, 2);
        builder.HasIndex(product => product.ExternalId).IsUnique();
        builder.HasIndex(product => product.Sku);
    }
}
