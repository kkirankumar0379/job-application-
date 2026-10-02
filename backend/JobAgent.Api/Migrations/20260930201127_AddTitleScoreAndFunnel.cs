using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobAgent.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTitleScoreAndFunnel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TitleScore",
                table: "JobPostings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FunnelJson",
                table: "DiscoveryRuns",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TitleScore",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "FunnelJson",
                table: "DiscoveryRuns");
        }
    }
}
