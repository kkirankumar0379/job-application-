using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobAgent.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTailoredResumes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TailoredResumes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobPostingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentJson = table.Column<string>(type: "text", nullable: false),
                    ConfirmedSkillsCsv = table.Column<string>(type: "text", nullable: false),
                    AtsScoreBefore = table.Column<int>(type: "integer", nullable: false),
                    AtsScoreAfter = table.Column<int>(type: "integer", nullable: false),
                    FilePath = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TailoredResumes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TailoredResumes_CandidateProfileId_JobPostingId",
                table: "TailoredResumes",
                columns: new[] { "CandidateProfileId", "JobPostingId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TailoredResumes");
        }
    }
}
