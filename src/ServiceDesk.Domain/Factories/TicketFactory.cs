using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Rules;

namespace ServiceDesk.Domain.Factories;

/// <summary>Everything the requester typed on the intake form.</summary>
public sealed record NewTicketSpec(
    Urgency Urgency,
    Impact Impact,
    string Subject,
    string Description,
    string? AssetTag = null,
    string? DeviceType = null,
    string? ApplicationName = null,
    string? ApplicationVersion = null,
    string? NetworkLocation = null,
    string? AffectedIpAddress = null);

/// <summary>Factory Method: the category type decides which concrete Ticket subclass is created.</summary>
public interface ITicketFactory
{
    /// <summary>Empty instance used by the DAL for rehydration.</summary>
    Ticket CreateEmpty(TicketCategoryType type);

    /// <summary>New, validated ticket in status Queued with SLA deadlines.</summary>
    Ticket CreateNew(int requesterId, TicketCategory category, SlaPolicy policy, NewTicketSpec spec, DateTime nowUtc);
}

public sealed class TicketFactory : ITicketFactory
{
    public Ticket CreateEmpty(TicketCategoryType type) => type switch
    {
        TicketCategoryType.Hardware => new HardwareTicket(),
        TicketCategoryType.Software => new SoftwareTicket(),
        TicketCategoryType.Network => new NetworkTicket(),
        TicketCategoryType.General => new GeneralTicket(),
        _ => throw new DomainException($"Unknown ticket category type: {type}")
    };

    public Ticket CreateNew(int requesterId, TicketCategory category, SlaPolicy policy, NewTicketSpec spec, DateTime nowUtc)
    {
        var expected = PriorityMatrix.Calculate(spec.Urgency, spec.Impact);
        if (policy.Priority != expected)
            throw new DomainException($"SLA policy {policy.Priority} does not match computed priority {expected}.");

        var ticket = CreateEmpty(category.CategoryType);
        ApplyDetails(ticket, spec);
        ticket.InitializeNew(requesterId, category, spec.Urgency, spec.Impact, spec.Subject, spec.Description, policy, nowUtc);

        var errors = ticket.Validate();
        if (errors.Count > 0) throw new ValidationException(errors);
        return ticket;
    }

    private static void ApplyDetails(Ticket ticket, NewTicketSpec spec)
    {
        switch (ticket)
        {
            case HardwareTicket hw:
                hw.AssetTag = Clean(spec.AssetTag);
                hw.DeviceType = Clean(spec.DeviceType);
                break;
            case SoftwareTicket sw:
                sw.ApplicationName = Clean(spec.ApplicationName);
                sw.ApplicationVersion = Clean(spec.ApplicationVersion);
                break;
            case NetworkTicket nw:
                nw.NetworkLocation = Clean(spec.NetworkLocation);
                nw.AffectedIpAddress = Clean(spec.AffectedIpAddress);
                break;
        }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
