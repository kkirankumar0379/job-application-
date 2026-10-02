using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobAgent.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddExcludeFederalJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExcludeFederalJobs",
                table: "SearchPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExcludeFederalJobs",
                table: "SearchPreferences");
        }
    }
}
