using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupplierIntegrationApi.Entities;

namespace SupplierIntegrationApi.Data.Configurations;

public class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("WebhookEvents");
        builder.HasKey(webhookEvent => webhookEvent.Id);
        builder.Property(webhookEvent => webhookEvent.ExternalEventId).HasMaxLength(128).IsRequired();
        builder.Property(webhookEvent => webhookEvent.EventType).HasMaxLength(128).IsRequired();
        builder.Property(webhookEvent => webhookEvent.ProductExternalId).HasMaxLength(128);
        builder.Property(webhookEvent => webhookEvent.FailureCode).HasMaxLength(64);
        builder.HasIndex(webhookEvent => webhookEvent.ExternalEventId).IsUnique();
    }
}
