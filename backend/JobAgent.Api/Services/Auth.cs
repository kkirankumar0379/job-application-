using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using JobAgent.Api.Data;
using JobAgent.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace JobAgent.Api.Services;

/// <summary>Salted PBKDF2 password hashes in the form "v1.iterations.salt.hash".</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations)) return false;
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>
/// Signs and validates login tokens. The signing key comes from the JWT_KEY env var / "Jwt:Key" config; with neither
/// (local use) a random key is generated once and saved next to the other local secrets.
/// </summary>
public sealed class TokenService(IConfiguration config)
{
    private const string SecretName = "jwtKey";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public SymmetricSecurityKey Key { get; } = LoadKey(config);

    private static SymmetricSecurityKey LoadKey(IConfiguration config)
    {
        var configured = Environment.GetEnvironmentVariable("JWT_KEY") is { Length: > 0 } e ? e : config["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = LocalSecrets.Get(SecretName);
            if (configured is null)
            {
                configured = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
                LocalSecrets.Set(SecretName, configured);
            }
        }
        if (configured.Length < 32) throw new InvalidOperationException("JWT_KEY must be at least 32 characters.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configured));
    }

    public string Create(AppUser user) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        claims: [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Email, user.Email), new Claim("admin", user.IsAdmin ? "1" : "0")],
        expires: DateTime.UtcNow.Add(Lifetime),
        signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));
}

public static class UserExtensions
{
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    public static bool IsAdmin(this ClaimsPrincipal user) => user.FindFirstValue("admin") == "1";

    /// <summary>The profile, only if it belongs to the signed-in user (null otherwise, so callers answer 404).</summary>
    public static Task<CandidateProfile?> OwnedProfile(this AppDbContext db, ClaimsPrincipal user, Guid profileId, CancellationToken ct = default)
    {
        var uid = user.UserId();
        return db.CandidateProfiles.FirstOrDefaultAsync(p => p.Id == profileId && p.UserId == uid, ct);
    }

    public static Task<bool> OwnsProfile(this AppDbContext db, ClaimsPrincipal user, Guid profileId, CancellationToken ct = default)
    {
        var uid = user.UserId();
        return db.CandidateProfiles.AnyAsync(p => p.Id == profileId && p.UserId == uid, ct);
    }

    public static IQueryable<Guid> OwnedProfileIds(this AppDbContext db, ClaimsPrincipal user)
    {
        var uid = user.UserId();
        return db.CandidateProfiles.Where(p => p.UserId == uid).Select(p => p.Id);
    }

    /// <summary>The job, only if it was found for (or added by) one of the signed-in user's profiles.</summary>
    public static async Task<JobPosting?> OwnedJob(this AppDbContext db, ClaimsPrincipal user, Guid jobId, CancellationToken ct = default)
    {
        var mine = db.OwnedProfileIds(user);
        return await db.JobPostings.FirstOrDefaultAsync(j => j.Id == jobId
            && ((j.CandidateProfileId != null && mine.Contains(j.CandidateProfileId.Value))
                || (j.AddedByProfileId != null && mine.Contains(j.AddedByProfileId.Value))), ct);
    }
}
