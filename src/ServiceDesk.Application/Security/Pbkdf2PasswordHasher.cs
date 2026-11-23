using System.Security.Cryptography;
using System.Text;
using ServiceDesk.Application.Abstractions;

namespace ServiceDesk.Application.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 (OWASP 2023 recommendation: 600,000 iterations), 128-bit salt, 256-bit key.
/// Verification uses constant-time comparison to avoid timing side-channels.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    public const int DefaultIterations = 600_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    private readonly int _iterations;

    public Pbkdf2PasswordHasher(int iterations = DefaultIterations) => _iterations = iterations;

    public PasswordHashResult Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, _iterations, HashAlgorithmName.SHA256, KeySize);
        return new PasswordHashResult(hash, salt, _iterations);
    }

    public bool Verify(string password, byte[] hash, byte[] salt, int iterations)
    {
        if (string.IsNullOrEmpty(password) || hash.Length == 0 || salt.Length == 0 || iterations <= 0) return false;
        var candidate = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, hash.Length);
        return CryptographicOperations.FixedTimeEquals(candidate, hash);
    }

    /// <summary>Admin password policy: ≥ 10 chars with upper, lower, digit and symbol.</summary>
    public static IReadOnlyList<string> CheckPolicy(string? password)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(password) || password.Length < 10) errors.Add("Password must be at least 10 characters.");
        if (password is null) return errors;
        if (!password.Any(char.IsUpper)) errors.Add("Password must contain an upper-case letter.");
        if (!password.Any(char.IsLower)) errors.Add("Password must contain a lower-case letter.");
        if (!password.Any(char.IsDigit)) errors.Add("Password must contain a digit.");
        if (password.All(char.IsLetterOrDigit)) errors.Add("Password must contain a symbol.");
        return errors;
    }
}
