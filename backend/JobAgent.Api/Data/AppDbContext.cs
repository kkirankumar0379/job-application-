using JobAgent.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobAgent.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<CandidateProfile> CandidateProfiles => Set<CandidateProfile>();
    public DbSet<SavedAnswer> SavedAnswers => Set<SavedAnswer>();
    public DbSet<JobPosting> JobPostings => Set<JobPosting>();
    public DbSet<JobApplication> JobApplications => Set<JobApplication>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SavedAnswer>().HasIndex(x => new { x.CandidateProfileId, x.Key }).IsUnique();
        modelBuilder.Entity<JobPosting>().HasIndex(x => x.ApplyUrl);
    }
}
