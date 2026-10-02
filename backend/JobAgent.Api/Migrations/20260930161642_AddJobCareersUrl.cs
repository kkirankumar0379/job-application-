using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobAgent.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddJobCareersUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CareersUrl",
                table: "JobPostings",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Backfill existing jobs from their company's board: same provider, and the apply link
            // contains the board token (or, for custom-domain career sites, the company name matches).
            migrationBuilder.Sql("""
                UPDATE "JobPostings" j
                SET "CareersUrl" = CASE s."AtsProvider"
                    WHEN 'Greenhouse' THEN 'https://job-boards.greenhouse.io/' || s."BoardToken"
                    WHEN 'Lever' THEN 'https://jobs.lever.co/' || s."BoardToken"
                    WHEN 'Ashby' THEN 'https://jobs.ashbyhq.com/' || s."BoardToken"
                    WHEN 'SmartRecruiters' THEN 'https://jobs.smartrecruiters.com/' || s."BoardToken"
                    WHEN 'Workday' THEN 'https://' || s."BoardToken"
                    ELSE '' END
                FROM "CompanySources" s
                WHERE j."AtsProvider" = s."AtsProvider"
                  AND (j."ApplyUrl" ILIKE '%/' || s."BoardToken" || '/%'
                       OR j."ApplyUrl" ILIKE 'https://' || s."BoardToken" || '%'
                       OR lower(j."Company") = lower(s."Name"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CareersUrl",
                table: "JobPostings");
        }
    }
}
