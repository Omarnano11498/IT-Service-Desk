using ServiceDesk.Domain;
using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Application.Models;

public sealed class TicketFilter
{
    public TicketStatus? Status { get; init; }
    public TicketPriority? Priority { get; init; }
    public short? CategoryId { get; init; }
    public int? AssignedAdminId { get; init; }
    public string? SearchText { get; init; }
    public bool OpenOnly { get; init; } = true;
    public int MaxRows { get; init; } = 2000;
}

public sealed class SubmitTicketRequest
{
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Department { get; init; }
    public short CategoryId { get; init; }
    public Urgency Urgency { get; init; } = Urgency.Medium;
    public Impact Impact { get; init; } = Impact.Medium;
    public string Subject { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? AssetTag { get; init; }
    public string? DeviceType { get; init; }
    public string? ApplicationName { get; init; }
    public string? ApplicationVersion { get; init; }
    public string? NetworkLocation { get; init; }
    public string? AffectedIpAddress { get; init; }
}

public sealed record SubmitTicketResult(string TicketNumber, TicketPriority Priority, DateTime ResponseDueAtUtc, DateTime ResolutionDueAtUtc);

public sealed record TicketDetail(Ticket Ticket, IReadOnlyList<TicketHistoryEntry> History, ServiceEvaluation? Evaluation);

/// <summary>One ranked row of the admin queue.</summary>
public sealed record QueueItem(
    int Rank,
    Ticket Ticket,
    double UrgencyScore,
    SlaState SlaState,
    bool ResponseOverdue,
    TimeSpan RemainingResolution,
    TimeSpan Age);

public sealed record QueueSnapshot(IReadOnlyList<QueueItem> Items, DateTime AsOfUtc, string StrategyName, TimeSpan SortDuration);

// ------------------------- Statistics -------------------------

public sealed record BreakdownItem(string Label, int Count, double Percent);

public sealed record DurationStats(int Count, double MeanHours, double MedianHours, double MinHours, double MaxHours, double StdDevHours, double P90Hours)
{
    public static DurationStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

public sealed class StatisticsReport
{
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public DateTime GeneratedAtUtc { get; init; }

    public int Total { get; init; }
    public int Open { get; init; }
    public int InProgress { get; init; }
    public int Pending { get; init; }
    public int ResolvedOrClosed { get; init; }
    public int OpenBreached { get; init; }
    public int OpenAtRisk { get; init; }

    public IReadOnlyList<BreakdownItem> ByStatus { get; init; } = Array.Empty<BreakdownItem>();
    public IReadOnlyList<BreakdownItem> ByCategory { get; init; } = Array.Empty<BreakdownItem>();
    public IReadOnlyList<BreakdownItem> ByPriority { get; init; } = Array.Empty<BreakdownItem>();
    public IReadOnlyList<BreakdownItem> ByAdministrator { get; init; } = Array.Empty<BreakdownItem>();
    public IReadOnlyList<BreakdownItem> ByGrade { get; init; } = Array.Empty<BreakdownItem>();

    public DurationStats Resolution { get; init; } = DurationStats.Empty;
    public DurationStats FirstResponse { get; init; } = DurationStats.Empty;

    public double SlaCompliancePercent { get; init; }
    public double ResponseCompliancePercent { get; init; }
    public double MeanEvaluationScore { get; init; }
    public double ServiceHealthIndex { get; init; }
    public string ServiceHealthLabel { get; init; } = "n/a";

    public IReadOnlyList<ServiceEvaluation> Evaluations { get; init; } = Array.Empty<ServiceEvaluation>();
}
