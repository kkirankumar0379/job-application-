using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobAgent.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAnswerMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnswerMemories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalQuestion = table.Column<string>(type: "text", nullable: false),
                    NormalizedQuestion = table.Column<string>(type: "text", nullable: false),
                    Intent = table.Column<string>(type: "text", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    Inverted = table.Column<bool>(type: "boolean", nullable: false),
                    Answer = table.Column<string>(type: "text", nullable: false),
                    AnswerType = table.Column<string>(type: "text", nullable: false),
                    OptionsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TimesUsed = table.Column<int>(type: "integer", nullable: false),
                    SourceCompany = table.Column<string>(type: "text", nullable: false),
                    SourceUrl = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Reliability = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnswerMemories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnswerMemories_CandidateProfileId_Intent_Subject",
                table: "AnswerMemories",
                columns: new[] { "CandidateProfileId", "Intent", "Subject" });

            migrationBuilder.CreateIndex(
                name: "IX_AnswerMemories_CandidateProfileId_NormalizedQuestion",
                table: "AnswerMemories",
                columns: new[] { "CandidateProfileId", "NormalizedQuestion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnswerMemories");
        }
    }
}
