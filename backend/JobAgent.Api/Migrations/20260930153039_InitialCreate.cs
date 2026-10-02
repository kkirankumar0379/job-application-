using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobAgent.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CandidateProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstName = table.Column<string>(type: "text", nullable: false),
                    LastName = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: false),
                    City = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Country = table.Column<string>(type: "text", nullable: false),
                    LinkedInUrl = table.Column<string>(type: "text", nullable: false),
                    ResumePath = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    SkillsCsv = table.Column<string>(type: "text", nullable: false),
                    YearsOfExperience = table.Column<int>(type: "integer", nullable: true),
                    ResumeText = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CompanySources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    AtsProvider = table.Column<string>(type: "text", nullable: false),
                    BoardToken = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastScannedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanySources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveryRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompaniesScanned = table.Column<int>(type: "integer", nullable: false),
                    CompaniesFailed = table.Column<int>(type: "integer", nullable: false),
                    JobsSeen = table.Column<int>(type: "integer", nullable: false),
                    JobsMatched = table.Column<int>(type: "integer", nullable: false),
                    JobsSaved = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobPostingId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AtsProvider = table.Column<string>(type: "text", nullable: false),
                    ResumePathUsed = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobPostings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Company = table.Column<string>(type: "text", nullable: false),
                    Location = table.Column<string>(type: "text", nullable: false),
                    SourceUrl = table.Column<string>(type: "text", nullable: false),
                    ApplyUrl = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    MatchScore = table.Column<int>(type: "integer", nullable: false),
                    MatchReason = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    AtsProvider = table.Column<string>(type: "text", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsRemote = table.Column<bool>(type: "boolean", nullable: false),
                    IsDismissed = table.Column<bool>(type: "boolean", nullable: false),
                    IsSaved = table.Column<bool>(type: "boolean", nullable: false),
                    SkillScore = table.Column<int>(type: "integer", nullable: false),
                    ExperienceScore = table.Column<int>(type: "integer", nullable: false),
                    RequiredYears = table.Column<int>(type: "integer", nullable: true),
                    MatchedSkillsCsv = table.Column<string>(type: "text", nullable: false),
                    MissingSkillsCsv = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobPostings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SavedAnswers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    QuestionPattern = table.Column<string>(type: "text", nullable: false),
                    Answer = table.Column<string>(type: "text", nullable: false),
                    IsSensitive = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresReviewEveryTime = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedAnswers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SearchPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    RoleKeywordsCsv = table.Column<string>(type: "text", nullable: false),
                    ExcludeKeywordsCsv = table.Column<string>(type: "text", nullable: false),
                    LocationsCsv = table.Column<string>(type: "text", nullable: false),
                    RemoteOnly = table.Column<bool>(type: "boolean", nullable: false),
                    ExperienceLevel = table.Column<string>(type: "text", nullable: false),
                    NeedsSponsorship = table.Column<bool>(type: "boolean", nullable: false),
                    MaxAgeHours = table.Column<int>(type: "integer", nullable: false),
                    ScanEveryHours = table.Column<int>(type: "integer", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchPreferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanySources_AtsProvider_BoardToken",
                table: "CompanySources",
                columns: new[] { "AtsProvider", "BoardToken" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_ApplyUrl",
                table: "JobPostings",
                column: "ApplyUrl");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_CandidateProfileId_CreatedAt",
                table: "JobPostings",
                columns: new[] { "CandidateProfileId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedAnswers_CandidateProfileId_Key",
                table: "SavedAnswers",
                columns: new[] { "CandidateProfileId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchPreferences_CandidateProfileId",
                table: "SearchPreferences",
                column: "CandidateProfileId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CandidateProfiles");

            migrationBuilder.DropTable(
                name: "CompanySources");

            migrationBuilder.DropTable(
                name: "DiscoveryRuns");

            migrationBuilder.DropTable(
                name: "JobApplications");

            migrationBuilder.DropTable(
                name: "JobPostings");

            migrationBuilder.DropTable(
                name: "SavedAnswers");

            migrationBuilder.DropTable(
                name: "SearchPreferences");
        }
    }
}
