using System.Collections.Concurrent;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.Security;
using ServiceDesk.Domain;
using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Application.Services;

/// <summary>IT administrator authentication with PBKDF2 verification and brute-force throttling.</summary>
public sealed class AuthService
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(1);

    private readonly IUnitOfWorkFactory _uowFactory;
    private readonly IPasswordHasher _hasher;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, (int Failures, DateTime? LockedUntil)> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public AuthService(IUnitOfWorkFactory uowFactory, IPasswordHasher hasher, IClock clock)
    {
        _uowFactory = uowFactory;
        _hasher = hasher;
        _clock = clock;
    }

    /// <summary>Returns the admin on success; throws <see cref="DomainException"/> with a generic message otherwise.</summary>
    public async Task<ITAdministrator> LoginAsync(string userName, string password, CancellationToken ct = default)
    {
        const string generic = "Invalid username or password.";
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password)) throw new DomainException(generic);

        var key = userName.Trim();
        var now = _clock.UtcNow;
        if (_attempts.TryGetValue(key, out var state) && state.LockedUntil > now)
            throw new DomainException($"Too many failed attempts. Try again in {(int)Math.Ceiling((state.LockedUntil.Value - now).TotalSeconds)} s.");

        await using var uow = await _uowFactory.CreateAsync(transactional: false, ct);
        var admin = await uow.Users.GetAdminByUserNameAsync(key, ct);

        // PBKDF2 is CPU-heavy (600k iterations) → run off the UI thread.
        var ok = false;
        if (admin is { IsActive: true } candidate)
            ok = await Task.Run(() => _hasher.Verify(password, candidate.PasswordHash, candidate.PasswordSalt, candidate.HashIterations), ct);

        if (!ok || admin is null)
        {
            RegisterFailure(key, now);
            throw new DomainException(generic);
        }

        _attempts.TryRemove(key, out _);
        await uow.Users.UpdateLastLoginAsync(admin.Id, now, ct);
        return admin;
    }

    public async Task ChangePasswordAsync(ITAdministrator admin, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var policyErrors = Pbkdf2PasswordHasher.CheckPolicy(newPassword);
        if (policyErrors.Count > 0) throw new ValidationException(policyErrors);
        if (currentPassword == newPassword) throw new DomainException("The new password must differ from the current one.");

        await using var uow = await _uowFactory.CreateAsync(transactional: true, ct);
        var fresh = await uow.Users.GetAdminByIdAsync(admin.Id, ct) ?? throw new DomainException("Administrator not found.");
        var ok = await Task.Run(() => _hasher.Verify(currentPassword, fresh.PasswordHash, fresh.PasswordSalt, fresh.HashIterations), ct);
        if (!ok) throw new DomainException("The current password is incorrect.");

        var hashed = await Task.Run(() => _hasher.Hash(newPassword), ct);
        await uow.Users.UpdatePasswordAsync(admin.Id, hashed.Hash, hashed.Salt, hashed.Iterations, ct);
        await uow.CommitAsync(ct);
    }

    private void RegisterFailure(string key, DateTime now) =>
        _attempts.AddOrUpdate(key,
            _ => (1, null),
            (_, s) => s.Failures + 1 >= MaxFailures ? (0, now + LockoutDuration) : (s.Failures + 1, s.LockedUntil));
}
