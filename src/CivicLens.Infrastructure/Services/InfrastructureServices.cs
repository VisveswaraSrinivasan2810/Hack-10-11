using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CivicLens.Application.Abstractions;
using CivicLens.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CivicLens.Infrastructure.Services;

public sealed class JwtOptions
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "civiclens";
    public string Audience { get; set; } = "civiclens-clients";
    public int ExpiryMinutes { get; set; } = 480;
}

public sealed class StorageOptions
{
    public string Path { get; set; } = "uploads";
}

public sealed class BCryptPasswordHasher : IPasswordHasher
{
    // Work factor 11: strong enough for an MVP, fast enough that demo logins feel instant.
    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, 11);

    public bool Verify(string password, string hash)
    {
        try { return BCrypt.Net.BCrypt.Verify(password, hash); }
        catch (Exception) { return false; }
    }
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock) : ITokenService
{
    private readonly JwtOptions _o = options.Value;

    public (string Token, DateTime ExpiresAt) Create(User user)
    {
        var expires = clock.GetUtcNow().UtcDateTime.AddMinutes(_o.ExpiryMinutes);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        };
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.Key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(_o.Issuer, _o.Audience, claims, expires: expires, signingCredentials: creds);
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

/// <summary>Local-disk storage for the MVP. Swap for S3/Azure Blob behind <see cref="IImageStorage"/>.</summary>
public sealed class LocalImageStorage(IOptions<StorageOptions> options) : IImageStorage
{
    private readonly string _root = Path.GetFullPath(options.Value.Path);

    public async Task<string> SaveAsync(byte[] bytes, string extension, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        var ext = extension is ".jpg" or ".png" or ".webp" ? extension : ".jpg"; // never trust caller-supplied extensions
        var name = $"{Guid.NewGuid():N}{ext}";
        await File.WriteAllBytesAsync(Path.Combine(_root, name), bytes, ct);
        return $"/uploads/{name}"; // public URL only — the filesystem path is never exposed
    }
}
