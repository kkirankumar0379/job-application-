using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobAgent.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBlockedCompanies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlockedCompaniesCsv",
                table: "SearchPreferences",
                type: "text",
                nullable: false,
                defaultValue: JobAgent.Api.Domain.SearchPreferences.DefaultBlockedCompanies);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockedCompaniesCsv",
                table: "SearchPreferences");
        }
    }
}
