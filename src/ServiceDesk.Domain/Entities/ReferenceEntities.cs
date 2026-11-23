namespace ServiceDesk.Domain.Entities;

/// <summary>SLA targets for one priority level (dbo.SlaPolicies).</summary>
public sealed class SlaPolicy
{
    public TicketPriority Priority { get; internal set; }
    public string PriorityName { get; internal set; } = string.Empty;
    public int ResponseTargetMinutes { get; internal set; }
    public int ResolutionTargetMinutes { get; internal set; }
    public bool IsActive { get; internal set; } = true;

    public TimeSpan ResponseTarget => TimeSpan.FromMinutes(ResponseTargetMinutes);
    public TimeSpan ResolutionTarget => TimeSpan.FromMinutes(ResolutionTargetMinutes);

    public static SlaPolicy Create(TicketPriority priority, int responseMinutes, int resolutionMinutes)
    {
        if (responseMinutes <= 0) throw new DomainException("Response target must be positive.");
        if (resolutionMinutes < responseMinutes) throw new DomainException("Resolution target must be >= response target.");
        return new SlaPolicy
        {
            Priority = priority,
            PriorityName = priority.ToString(),
            ResponseTargetMinutes = responseMinutes,
            ResolutionTargetMinutes = resolutionMinutes
        };
    }

    /// <summary>Updates targets (admin-configurable SLA policy table).</summary>
    public void UpdateTargets(int responseMinutes, int resolutionMinutes)
    {
        var validated = Create(Priority, responseMinutes, resolutionMinutes);
        ResponseTargetMinutes = validated.ResponseTargetMinutes;
        ResolutionTargetMinutes = validated.ResolutionTargetMinutes;
    }
}

/// <summary>dbo.TicketCategories row.</summary>
public sealed class TicketCategory
{
    public short Id { get; internal set; }
    public string Name { get; internal set; } = string.Empty;
    public TicketCategoryType CategoryType { get; internal set; }
    public string? Description { get; internal set; }
    public bool IsActive { get; internal set; } = true;

    public override string ToString() => Name;
}

/// <summary>Immutable audit entry (dbo.TicketHistory). Created only by domain operations.</summary>
public sealed class TicketHistoryEntry
{
    public long Id { get; internal set; }
    public int TicketId { get; internal set; }
    public HistoryAction Action { get; internal set; }
    public TicketStatus? FromStatus { get; internal set; }
    public TicketStatus? ToStatus { get; internal set; }
    public TicketPriority? FromPriority { get; internal set; }
    public TicketPriority? ToPriority { get; internal set; }
    public int? PerformedByUserId { get; internal set; }
    public string? PerformedByName { get; internal set; }
    public string? Notes { get; internal set; }
    public bool IsInternal { get; internal set; }
    public DateTime OccurredAt { get; internal set; }

    internal TicketHistoryEntry() { }

    internal static TicketHistoryEntry Create(HistoryAction action, int? userId, DateTime occurredAt,
        string? notes = null, TicketStatus? from = null, TicketStatus? to = null,
        TicketPriority? fromPriority = null, TicketPriority? toPriority = null, bool isInternal = false) => new()
    {
        Action = action,
        PerformedByUserId = userId,
        OccurredAt = occurredAt,
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        FromStatus = from,
        ToStatus = to,
        FromPriority = fromPriority,
        ToPriority = toPriority,
        IsInternal = isInternal
    };

    public string Summary => Action switch
    {
        HistoryAction.StatusChanged or HistoryAction.Assigned or HistoryAction.Escalated
            or HistoryAction.Resolved or HistoryAction.Closed or HistoryAction.Reopened
            when FromStatus is not null && ToStatus is not null
            => $"{Action}: {FromStatus.Value.ToDisplay()} → {ToStatus.Value.ToDisplay()}",
        HistoryAction.PriorityChanged => $"Priority: {FromPriority} → {ToPriority}",
        _ => Action.ToString()
    };
}

/// <summary>Persisted service evaluation snapshot (dbo.ServiceEvaluations).</summary>
public sealed class ServiceEvaluation
{
    public int Id { get; internal set; }
    public int TicketId { get; internal set; }
    public string TicketNumber { get; internal set; } = string.Empty;
    public string? AdminName { get; internal set; }
    public TicketPriority Priority { get; internal set; }
    public decimal SpeedScore { get; internal set; }
    public decimal QualityScore { get; internal set; }
    public decimal OverallScore { get; internal set; }
    public EvaluationGrade Grade { get; internal set; }
    public bool SlaBreached { get; internal set; }
    public byte? SatisfactionRating { get; internal set; }
    public string? RequesterComment { get; internal set; }
    public string FormulaVersion { get; internal set; } = "v1";
    public DateTime EvaluatedAt { get; internal set; }
    public double? ResolutionHours { get; internal set; }

    public static ServiceEvaluation FromResult(int ticketId, Strategies.EvaluationResult r,
        byte? rating, string? comment, string formulaVersion, DateTime nowUtc) => new()
    {
        TicketId = ticketId,
        SpeedScore = (decimal)Math.Round(r.SpeedScore, 2),
        QualityScore = (decimal)Math.Round(r.QualityScore, 2),
        OverallScore = (decimal)Math.Round(r.OverallScore, 2),
        Grade = r.Grade,
        SlaBreached = r.SlaBreached,
        SatisfactionRating = rating,
        RequesterComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
        FormulaVersion = formulaVersion,
        EvaluatedAt = nowUtc
    };
}
