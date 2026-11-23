using System.Net;
using ServiceDesk.Domain.Rules;

namespace ServiceDesk.Domain.Entities;

/// <summary>
/// Aggregate root of the service desk. State changes go through intention-revealing methods
/// (Assign, StartWork, Resolve...) guarded by <see cref="TicketStateMachine"/>; every change
/// appends an immutable <see cref="TicketHistoryEntry"/> that the service layer persists
/// in the same transaction (audit trail cannot be forgotten).
/// </summary>
public abstract class Ticket : EntityBase
{
    private readonly List<TicketHistoryEntry> _pendingHistory = new();

    public const int SubjectMaxLength = 200;
    public const int DescriptionMaxLength = 8000;

    public string TicketNumber { get; internal set; } = string.Empty;

    public int RequesterId { get; internal set; }
    public string RequesterName { get; internal set; } = string.Empty;   // read-model projection
    public string RequesterEmail { get; internal set; } = string.Empty;  // read-model projection

    public short CategoryId { get; internal set; }
    public string CategoryName { get; internal set; } = string.Empty;
    public abstract TicketCategoryType CategoryType { get; }

    public TicketPriority Priority { get; internal set; }
    public TicketStatus Status { get; internal set; }
    public Urgency Urgency { get; internal set; }
    public Impact Impact { get; internal set; }

    public string Subject { get; internal set; } = string.Empty;
    public string Description { get; internal set; } = string.Empty;

    public int? AssignedAdminId { get; internal set; }
    public string? AssignedAdminName { get; internal set; }

    public DateTime CreatedAt { get; internal set; }
    public DateTime? FirstResponseAt { get; internal set; }
    public DateTime ResponseDueAt { get; internal set; }
    public DateTime ResolutionDueAt { get; internal set; }
    public DateTime? ResolvedAt { get; internal set; }
    public DateTime? ClosedAt { get; internal set; }

    public string? ResolutionNotes { get; internal set; }
    public string? RootCause { get; internal set; }

    public byte[] RowVersion { get; internal set; } = Array.Empty<byte>();

    // ---------------- Derived values ----------------
    public bool IsOpen => Status.IsOpen();
    public TimeSpan? ResolutionDuration => ResolvedAt - CreatedAt;
    public TimeSpan? TimeToFirstResponse => FirstResponseAt - CreatedAt;
    public TimeSpan Age(DateTime nowUtc) => (ResolvedAt ?? nowUtc) - CreatedAt;

    /// <summary>Polymorphic, category-specific details shown in the triage panel.</summary>
    public abstract string DetailsSummary { get; }

    /// <summary>Polymorphic suggestion for the first triage step.</summary>
    public abstract string SuggestedFirstAction { get; }

    public IReadOnlyList<TicketHistoryEntry> PendingHistory => _pendingHistory;

    /// <summary>Returns and clears the history produced since the last persist.</summary>
    public IReadOnlyList<TicketHistoryEntry> DequeuePendingHistory()
    {
        var copy = _pendingHistory.ToList();
        _pendingHistory.Clear();
        return copy;
    }

    // ---------------- Validation ----------------
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Subject)) errors.Add("Subject is required.");
        else if (Subject.Length > SubjectMaxLength) errors.Add($"Subject must be at most {SubjectMaxLength} characters.");
        if (string.IsNullOrWhiteSpace(Description)) errors.Add("Description is required.");
        else if (Description.Length > DescriptionMaxLength) errors.Add($"Description must be at most {DescriptionMaxLength} characters.");
        if (!Enum.IsDefined(Urgency)) errors.Add("Urgency is invalid.");
        if (!Enum.IsDefined(Impact)) errors.Add("Impact is invalid.");
        errors.AddRange(ValidateDetails());
        return errors;
    }

    /// <summary>Template-method hook: subtypes add their own rules.</summary>
    protected virtual IEnumerable<string> ValidateDetails() => Enumerable.Empty<string>();

    // ---------------- Lifecycle (ITIL) ----------------
    internal void InitializeNew(int requesterId, TicketCategory category, Urgency urgency, Impact impact,
        string subject, string description, SlaPolicy policy, DateTime nowUtc)
    {
        RequesterId = requesterId;
        CategoryId = category.Id;
        CategoryName = category.Name;
        Urgency = urgency;
        Impact = impact;
        Subject = subject?.Trim() ?? string.Empty;
        Description = description?.Trim() ?? string.Empty;
        Priority = policy.Priority;
        Status = TicketStatus.Queued;
        CreatedAt = TruncateToSecond(nowUtc);
        ApplySla(policy);
        _pendingHistory.Add(TicketHistoryEntry.Create(HistoryAction.Created, requesterId, CreatedAt,
            "Ticket submitted", to: TicketStatus.Queued, toPriority: Priority));
    }

    /// <summary>IT admin receives / claims the ticket.</summary>
    public void Assign(ITAdministrator admin, DateTime nowUtc, string? notes = null)
    {
        EnsureAdmin(admin);
        if (!IsOpen) throw new DomainException($"Ticket {TicketNumber} is {Status.ToDisplay()} and cannot be assigned.");
        if (AssignedAdminId == admin.Id && Status == TicketStatus.Assigned)
            throw new DomainException($"Ticket {TicketNumber} is already assigned to you.");

        var from = Status;
        if (Status is TicketStatus.Queued) TransitionGuard(TicketStatus.Assigned);
        AssignedAdminId = admin.Id;
        AssignedAdminName = admin.FullName;
        if (Status is TicketStatus.Queued) Status = TicketStatus.Assigned;
        MarkFirstResponse(nowUtc);
        _pendingHistory.Add(TicketHistoryEntry.Create(HistoryAction.Assigned, admin.Id, nowUtc,
            notes ?? $"Received by {admin.FullName}", from, Status));
    }

    public void StartWork(ITAdministrator admin, DateTime nowUtc, string? notes = null)
    {
        EnsureAdmin(admin);
        if (AssignedAdminId is null) { AssignedAdminId = admin.Id; AssignedAdminName = admin.FullName; }
        Transition(TicketStatus.InProgress, HistoryAction.StatusChanged, admin.Id, nowUtc, notes ?? "Work started");
    }

    public void PutOnHold(ITAdministrator admin, DateTime nowUtc, string reason)
    {
        EnsureAdmin(admin);
        RequireText(reason, "A reason is required to move a ticket to Pending.");
        Transition(TicketStatus.Pending, HistoryAction.StatusChanged, admin.Id, nowUtc, reason);
    }

    public void Escalate(ITAdministrator admin, DateTime nowUtc, string reason)
    {
        EnsureAdmin(admin);
        RequireText(reason, "An escalation note is required.");
        Transition(TicketStatus.Escalated, HistoryAction.Escalated, admin.Id, nowUtc, reason);
    }

    public void ReturnToQueue(ITAdministrator admin, DateTime nowUtc, string? notes = null)
    {
        EnsureAdmin(admin);
        Transition(TicketStatus.Queued, HistoryAction.StatusChanged, admin.Id, nowUtc, notes ?? "Returned to queue");
        AssignedAdminId = null;
        AssignedAdminName = null;
    }

    public void Resolve(ITAdministrator admin, DateTime nowUtc, string resolutionNotes, string rootCause)
    {
        EnsureAdmin(admin);
        RequireText(resolutionNotes, "Resolution notes are required.");
        RequireText(rootCause, "Root cause is required.");
        Transition(TicketStatus.Resolved, HistoryAction.Resolved, admin.Id, nowUtc, resolutionNotes);
        ResolutionNotes = resolutionNotes.Trim();
        RootCause = rootCause.Trim();
        ResolvedAt = TruncateToSecond(nowUtc);
        AssignedAdminId ??= admin.Id;
        AssignedAdminName ??= admin.FullName;
    }

    public void Close(ITAdministrator admin, DateTime nowUtc, string? notes = null)
    {
        EnsureAdmin(admin);
        Transition(TicketStatus.Closed, HistoryAction.Closed, admin.Id, nowUtc, notes ?? "Ticket closed");
        ClosedAt = TruncateToSecond(nowUtc);
    }

    public void Reopen(ITAdministrator admin, DateTime nowUtc, string reason)
    {
        EnsureAdmin(admin);
        RequireText(reason, "A reason is required to reopen a ticket.");
        Transition(TicketStatus.InProgress, HistoryAction.Reopened, admin.Id, nowUtc, reason);
        ResolvedAt = null;   // resolution time is re-measured on the next resolution
    }

    public void ChangePriority(ITAdministrator admin, SlaPolicy newPolicy, DateTime nowUtc, string? reason)
    {
        EnsureAdmin(admin);
        if (!IsOpen) throw new DomainException("Priority can only be changed on open tickets.");
        if (newPolicy.Priority == Priority) return;
        var from = Priority;
        Priority = newPolicy.Priority;
        ApplySla(newPolicy);   // SLA clock is anchored on CreatedAt, so re-prioritising cannot "reset" a breach
        _pendingHistory.Add(TicketHistoryEntry.Create(HistoryAction.PriorityChanged, admin.Id, nowUtc,
            reason ?? $"Priority changed to {newPolicy.Priority}", fromPriority: from, toPriority: Priority));
    }

    public void AddNote(int userId, DateTime nowUtc, string note, bool isInternal)
    {
        RequireText(note, "Note text is required.");
        if (note.Length > 2000) throw new DomainException("Notes are limited to 2000 characters.");
        _pendingHistory.Add(TicketHistoryEntry.Create(HistoryAction.NoteAdded, userId, nowUtc, note, isInternal: isInternal));
    }

    internal void RecordRating(int requesterId, DateTime nowUtc, byte rating, string? comment)
    {
        if (Status is not (TicketStatus.Resolved or TicketStatus.Closed))
            throw new DomainException("Only resolved or closed tickets can be rated.");
        if (rating is < 1 or > 5) throw new DomainException("Rating must be between 1 and 5.");
        _pendingHistory.Add(TicketHistoryEntry.Create(HistoryAction.Rated, requesterId, nowUtc,
            $"Requester rating: {rating}/5" + (string.IsNullOrWhiteSpace(comment) ? "" : $" — {comment.Trim()}")));
    }

    /// <summary>Public entry used by the service layer for requester ratings.</summary>
    public void Rate(int requesterId, DateTime nowUtc, byte rating, string? comment)
    {
        if (requesterId != RequesterId) throw new DomainException("Only the requester can rate this ticket.");
        RecordRating(requesterId, nowUtc, rating, comment);
    }

    // ---------------- Helpers ----------------
    private void Transition(TicketStatus target, HistoryAction action, int adminId, DateTime nowUtc, string? notes)
    {
        TransitionGuard(target);
        var from = Status;
        Status = target;
        if (from == TicketStatus.Queued) MarkFirstResponse(nowUtc);
        _pendingHistory.Add(TicketHistoryEntry.Create(action, adminId, nowUtc, notes, from, target));
    }

    private void TransitionGuard(TicketStatus target)
    {
        if (!TicketStateMachine.CanTransition(Status, target))
            throw new DomainException(
                $"Invalid transition for {TicketNumber}: {Status.ToDisplay()} → {target.ToDisplay()}. " +
                $"Allowed: {string.Join(", ", TicketStateMachine.AllowedTargets(Status).Select(s => s.ToDisplay()))}");
    }

    private void MarkFirstResponse(DateTime nowUtc) => FirstResponseAt ??= TruncateToSecond(nowUtc);

    private void ApplySla(SlaPolicy policy)
    {
        ResponseDueAt = CreatedAt + policy.ResponseTarget;
        ResolutionDueAt = CreatedAt + policy.ResolutionTarget;
    }

    private static void EnsureAdmin(ITAdministrator admin)
    {
        if (admin is null || !admin.CanManageTickets)
            throw new DomainException("Only an active IT administrator can perform this action.");
    }

    private static void RequireText(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DomainException(message);
    }

    /// <summary>DATETIME2(0) stores whole seconds; truncating keeps memory and DB consistent.</summary>
    internal static DateTime TruncateToSecond(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}

// ===================== Specialised tickets (inheritance + polymorphism) =====================

public sealed class HardwareTicket : Ticket
{
    public string? AssetTag { get; internal set; }
    public string? DeviceType { get; internal set; }

    public override TicketCategoryType CategoryType => TicketCategoryType.Hardware;

    public override string DetailsSummary =>
        $"Device: {DeviceType ?? "n/a"} | Asset tag: {AssetTag ?? "n/a"}";

    public override string SuggestedFirstAction =>
        "Verify the asset tag in inventory, check warranty, and run hardware diagnostics.";

    protected override IEnumerable<string> ValidateDetails()
    {
        if (string.IsNullOrWhiteSpace(DeviceType)) yield return "Device type is required for hardware tickets.";
        if (AssetTag?.Length > 50) yield return "Asset tag must be at most 50 characters.";
    }
}

public sealed class SoftwareTicket : Ticket
{
    public string? ApplicationName { get; internal set; }
    public string? ApplicationVersion { get; internal set; }

    public override TicketCategoryType CategoryType => TicketCategoryType.Software;

    public override string DetailsSummary =>
        $"Application: {ApplicationName ?? "n/a"} {ApplicationVersion}".TrimEnd();

    public override string SuggestedFirstAction =>
        "Reproduce the error, collect application logs, and check known-error / patch records.";

    protected override IEnumerable<string> ValidateDetails()
    {
        if (string.IsNullOrWhiteSpace(ApplicationName)) yield return "Application name is required for software tickets.";
        if (ApplicationVersion?.Length > 40) yield return "Application version must be at most 40 characters.";
    }
}

public sealed class NetworkTicket : Ticket
{
    public string? NetworkLocation { get; internal set; }
    public string? AffectedIpAddress { get; internal set; }

    public override TicketCategoryType CategoryType => TicketCategoryType.Network;

    public override string DetailsSummary =>
        $"Location: {NetworkLocation ?? "n/a"} | IP: {AffectedIpAddress ?? "n/a"}";

    public override string SuggestedFirstAction =>
        "Check monitoring dashboards, ping/traceroute the affected segment, and review recent changes.";

    protected override IEnumerable<string> ValidateDetails()
    {
        if (string.IsNullOrWhiteSpace(NetworkLocation)) yield return "Network location is required for network tickets.";
        if (!string.IsNullOrWhiteSpace(AffectedIpAddress) && !IPAddress.TryParse(AffectedIpAddress, out _))
            yield return "Affected IP address is not a valid IPv4/IPv6 address.";
    }
}

public sealed class GeneralTicket : Ticket
{
    public override TicketCategoryType CategoryType => TicketCategoryType.General;
    public override string DetailsSummary => "General service request";
    public override string SuggestedFirstAction => "Confirm the requester's identity before changing accounts or access.";
}
