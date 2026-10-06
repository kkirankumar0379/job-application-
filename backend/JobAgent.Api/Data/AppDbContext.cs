using JobAgent.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobAgent.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<CandidateProfile> CandidateProfiles => Set<CandidateProfile>();
    public DbSet<SavedAnswer> SavedAnswers => Set<SavedAnswer>();
    public DbSet<JobPosting> JobPostings => Set<JobPosting>();
    public DbSet<JobApplication> JobApplications => Set<JobApplication>();
    public DbSet<SearchPreferences> SearchPreferences => Set<SearchPreferences>();
    public DbSet<CompanySource> CompanySources => Set<CompanySource>();
    public DbSet<DiscoveryRun> DiscoveryRuns => Set<DiscoveryRun>();
    public DbSet<TailoredResume> TailoredResumes => Set<TailoredResume>();
    public DbSet<AnswerMemory> AnswerMemories => Set<AnswerMemory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<CandidateProfile>().HasIndex(x => x.UserId);
        modelBuilder.Entity<SavedAnswer>().HasIndex(x => new { x.CandidateProfileId, x.Key }).IsUnique();
        modelBuilder.Entity<JobPosting>().HasIndex(x => x.ApplyUrl);
        modelBuilder.Entity<JobPosting>().HasIndex(x => new { x.CandidateProfileId, x.CreatedAt });
        modelBuilder.Entity<SearchPreferences>().HasIndex(x => x.CandidateProfileId).IsUnique();
        modelBuilder.Entity<CompanySource>().HasIndex(x => new { x.AtsProvider, x.BoardToken }).IsUnique();
        modelBuilder.Entity<AnswerMemory>().HasIndex(x => new { x.CandidateProfileId, x.Intent, x.Subject });
        modelBuilder.Entity<AnswerMemory>().HasIndex(x => new { x.CandidateProfileId, x.NormalizedQuestion });
        modelBuilder.Entity<TailoredResume>().HasIndex(x => new { x.CandidateProfileId, x.JobPostingId });
    }
}
