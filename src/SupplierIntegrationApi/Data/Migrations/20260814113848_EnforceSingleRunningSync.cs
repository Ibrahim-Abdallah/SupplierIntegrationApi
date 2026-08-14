using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupplierIntegrationApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSingleRunningSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_SyncRuns_OneRunning",
                table: "SyncRuns",
                column: "Status",
                unique: true,
                filter: "[Status] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_SyncRuns_OneRunning",
                table: "SyncRuns");
        }
    }
}
