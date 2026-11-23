using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Domain.Rules;

/// <summary>
/// ITIL priority matrix: Priority = f(Urgency, Impact).
/// Score = urgency + impact (1 = High … 3 = Low) → 2 Critical, 3 High, 4 Medium, 5–6 Low.
/// O(1) time / O(1) space.
/// </summary>
public static class PriorityMatrix
{
    public static TicketPriority Calculate(Urgency urgency, Impact impact) =>
        ((int)urgency + (int)impact) switch
        {
            2 => TicketPriority.Critical,
            3 => TicketPriority.High,
            4 => TicketPriority.Medium,
            _ => TicketPriority.Low
        };
}

/// <summary>
/// Finite-state machine of the ticket lifecycle. Adjacency list lookup: O(1).
/// Queued → Assigned/InProgress/Pending/Escalated
/// Assigned → InProgress/Pending/Escalated/Queued
/// InProgress → Pending/Escalated/Resolved/Queued
/// Pending → InProgress/Escalated
/// Escalated → InProgress/Pending/Resolved
/// Resolved → Closed/InProgress(reopen)
/// Closed → (terminal)
/// </summary>
public static class TicketStateMachine
{
    private static readonly IReadOnlyDictionary<TicketStatus, TicketStatus[]> Transitions =
        new Dictionary<TicketStatus, TicketStatus[]>
        {
            [TicketStatus.Queued] = new[] { TicketStatus.Assigned, TicketStatus.InProgress, TicketStatus.Pending, TicketStatus.Escalated },
            [TicketStatus.Assigned] = new[] { TicketStatus.InProgress, TicketStatus.Pending, TicketStatus.Escalated, TicketStatus.Queued },
            [TicketStatus.InProgress] = new[] { TicketStatus.Pending, TicketStatus.Escalated, TicketStatus.Resolved, TicketStatus.Queued },
            [TicketStatus.Pending] = new[] { TicketStatus.InProgress, TicketStatus.Escalated },
            [TicketStatus.Escalated] = new[] { TicketStatus.InProgress, TicketStatus.Pending, TicketStatus.Resolved },
            [TicketStatus.Resolved] = new[] { TicketStatus.Closed, TicketStatus.InProgress },
            [TicketStatus.Closed] = Array.Empty<TicketStatus>()
        };

    public static bool CanTransition(TicketStatus from, TicketStatus to) =>
        Transitions.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static IReadOnlyList<TicketStatus> AllowedTargets(TicketStatus from) =>
        Transitions.TryGetValue(from, out var targets) ? targets : Array.Empty<TicketStatus>();
}

/// <summary>Live SLA evaluation for queue aging alerts. O(1) per ticket.</summary>
public static class SlaMonitor
{
    /// <summary>Fraction of the resolution window already consumed at <paramref name="nowUtc"/>.</summary>
    public const double AtRiskThreshold = 0.75;

    public static double ElapsedFraction(Ticket t, DateTime nowUtc)
    {
        var window = (t.ResolutionDueAt - t.CreatedAt).TotalMinutes;
        if (window <= 0) return 1.0;
        var end = t.ResolvedAt ?? nowUtc;
        return Math.Max(0, (end - t.CreatedAt).TotalMinutes / window);
    }

    public static SlaState Evaluate(Ticket t, DateTime nowUtc)
    {
        if (!t.IsOpen)
            return t.ResolvedAt is { } r && r <= t.ResolutionDueAt ? SlaState.Met : SlaState.Breached;

        var fraction = ElapsedFraction(t, nowUtc);
        return fraction >= 1.0 ? SlaState.Breached
             : fraction >= AtRiskThreshold ? SlaState.AtRisk
             : SlaState.OnTrack;
    }

    public static bool IsResponseOverdue(Ticket t, DateTime nowUtc) =>
        t.FirstResponseAt is null && t.IsOpen && nowUtc > t.ResponseDueAt;

    public static TimeSpan RemainingResolutionTime(Ticket t, DateTime nowUtc) =>
        t.ResolutionDueAt - (t.ResolvedAt ?? nowUtc);
}
