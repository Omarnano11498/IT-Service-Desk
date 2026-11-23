using System.Net.Mail;

namespace ServiceDesk.Domain.Entities;

/// <summary>Base for every persisted entity: identity + equality by Id.</summary>
public abstract class EntityBase
{
    public int Id { get; internal set; }

    public bool IsTransient => Id == 0;

    public override bool Equals(object? obj) =>
        obj is EntityBase other && other.GetType() == GetType() && !IsTransient && other.Id == Id;

    public override int GetHashCode() => IsTransient ? base.GetHashCode() : HashCode.Combine(GetType(), Id);
}

/// <summary>Immutable value object for requester contact details.</summary>
public sealed record ContactInfo
{
    public string Email { get; }
    public string? Phone { get; }

    public ContactInfo(string email, string? phone)
    {
        Email = (email ?? string.Empty).Trim().ToLowerInvariant();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
    }

    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254) return false;
        try
        {
            var addr = new MailAddress(email.Trim());
            return addr.Address.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase) && addr.Host.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Abstract user. Polymorphic capability checks replace role if/else chains in the UI.</summary>
public abstract class User : EntityBase
{
    public string FullName { get; internal set; } = string.Empty;
    public ContactInfo Contact { get; internal set; } = new(string.Empty, null);
    public string? Department { get; internal set; }
    public bool IsActive { get; internal set; } = true;
    public DateTime CreatedAt { get; internal set; }

    public abstract UserRole Role { get; }
    public virtual bool CanManageTickets => false;
    public virtual bool RequiresPassword => false;
}

/// <summary>Requester: identified by e-mail, no password (frictionless intake).</summary>
public sealed class EndUser : User
{
    public override UserRole Role => UserRole.EndUser;

    public static EndUser Create(string fullName, ContactInfo contact, string? department) => new()
    {
        FullName = fullName.Trim(),
        Contact = contact,
        Department = string.IsNullOrWhiteSpace(department) ? null : department.Trim()
    };
}

/// <summary>IT administrator: password-protected, can triage and resolve tickets.</summary>
public sealed class ITAdministrator : User
{
    public string UserName { get; internal set; } = string.Empty;
    public byte[] PasswordHash { get; internal set; } = Array.Empty<byte>();
    public byte[] PasswordSalt { get; internal set; } = Array.Empty<byte>();
    public int HashIterations { get; internal set; }
    public DateTime? LastLoginAt { get; internal set; }

    public override UserRole Role => UserRole.ITAdministrator;
    public override bool CanManageTickets => IsActive;
    public override bool RequiresPassword => true;
}
