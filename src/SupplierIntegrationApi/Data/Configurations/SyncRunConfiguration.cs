using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupplierIntegrationApi.Entities;

namespace SupplierIntegrationApi.Data.Configurations;

public class SyncRunConfiguration : IEntityTypeConfiguration<SyncRun>
{
    public void Configure(EntityTypeBuilder<SyncRun> builder)
    {
        builder.ToTable("SyncRuns");
        builder.HasKey(run => run.Id);
        builder.Property(run => run.FailureCode).HasMaxLength(64);
        builder.Property(run => run.FailureMessage).HasMaxLength(1024);
    }
}
