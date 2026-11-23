using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.Models;
using ServiceDesk.Domain;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Factories;
using ServiceDesk.Domain.Rules;

namespace ServiceDesk.Application.Services;

/// <summary>
/// Orchestrates the ITIL ticket lifecycle:
/// 1. Submission &amp; intake → 2. Validation, categorisation &amp; queueing → 3. Reception &amp; triage → 4. Resolution &amp; closure.
/// Every mutating use case runs inside one Unit of Work transaction: ticket update + audit entries + evaluation.
/// </summary>
public sealed class TicketService
{
    private readonly IUnitOfWorkFactory _uowFactory;
    private readonly ITicketFactory _ticketFactory;
    private readonly EvaluationService _evaluationService;
    private readonly IClock _clock;

    /// <summary>Observer pattern: raised after any committed change so open views can refresh.</summary>
    public event EventHandler<TicketChangedEventArgs>? TicketChanged;

    public TicketService(IUnitOfWorkFactory uowFactory, ITicketFactory ticketFactory, EvaluationService evaluationService, IClock clock)
    {
        _uowFactory = uowFactory;
        _ticketFactory = ticketFactory;
        _evaluationService = evaluationService;
        _clock = clock;
    }

    // =============================== End-user use cases ===============================

    public async Task<IReadOnlyList<TicketCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var uow = await _uowFactory.CreateAsync(transactional: false, ct);
        return await uow.Categories.GetActiveAsync(ct);
    }

    /// <summary>Steps 1–2: validate, compute priority + SLA, persist, audit, queue.</summary>
    public async Task<SubmitTicketResult> SubmitAsync(SubmitTicketRequest request, CancellationToken ct = default)
    {
        var errors = ValidateRequester(request);
        if (errors.Count > 0) throw new ValidationException(errors);

        var now = _clock.UtcNow;
        var priority = PriorityMatrix.Calculate(request.Urgency, request.Impact);
        var spec = new NewTicketSpec(request.Urgency, request.Impact, request.Subject, request.Description,
            request.AssetTag, request.DeviceType, request.ApplicationName, request.ApplicationVersion,
            request.NetworkLocation, request.AffectedIpAddress);

        await using var uow = await _uowFactory.CreateAsync(transactional: true, ct);

        var category = await uow.Categories.GetAsync(request.CategoryId, ct)
                       ?? throw new ValidationException(new[] { "Please choose a valid category." });
        var policy = await uow.SlaPolicies.GetAsync(priority, ct);

        var requester = EndUser.Create(request.FullName, new ContactInfo(request.Email, request.Phone), request.Department);
        var requesterId = await uow.Users.UpsertEndUserAsync(requester, ct);

        var ticket = _ticketFactory.CreateNew(requesterId, category, policy, spec, now);   // validates subtype rules
        var (_, number) = await uow.Tickets.InsertAsync(ticket, ct);   // repository assigns Id + TicketNumber

        await PersistHistoryAsync(uow, ticket, ct);
        await uow.CommitAsync(ct);

        OnTicketChanged(ticket, HistoryAction.Created);
        return new SubmitTicketResult(number, ticket.Priority, ticket.ResponseDueAt, ticket.ResolutionDueAt);
    }

    /// <summary>
    /// Requester status lookup. Requires ticket number AND the requester e-mail, so ticket numbers
    /// alone cannot be enumerated. Internal admin notes are filtered out.
    /// </summary>
    public async Task<TicketDetail?> TrackAsync(string ticketNumber, string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticketNumber) || !ContactInfo.IsValidEmail(email)) return null;

        await using var uow = await _uowFactory.CreateAsync(transactional: false, ct);
        var ticket = await uow.Tickets.GetByNumberAsync(NormalizeNumber(ticketNumber), ct);
        if (ticket is null || !string.Equals(ticket.RequesterEmail, email.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;

        var history = await uow.History.GetByTicketAsync(ticket.Id, includeInternal: false, ct);
        var evaluation = await uow.Evaluations.GetByTicketAsync(ticket.Id, ct);
        return new TicketDetail(ticket, history, evaluation);
    }

    /// <summary>Optional requester satisfaction rating (feeds the Quality score).</summary>
    public async Task RateAsync(string ticketNumber, string email, byte rating, string? comment, CancellationToken ct = default)
    {
        await using var uow = await _uowFactory.CreateAsync(transactional: true, ct);
        var ticket = await uow.Tickets.GetByNumberAsync(NormalizeNumber(ticketNumber), ct);
        if (ticket is null || !string.Equals(ticket.RequesterEmail, email.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Ticket not found for this e-mail address.");

        ticket.Rate(ticket.RequesterId, _clock.UtcNow, rating, comment);
        await PersistHistoryAsync(uow, ticket, ct);
        await _evaluationService.EvaluateAsync(uow, ticket.Id, rating, comment, ct);
        await uow.CommitAsync(ct);
        OnTicketChanged(ticket, HistoryAction.Rated);
    }

    // =============================== IT admin use cases ===============================

    public async Task<TicketDetail?> GetDetailAsync(int ticketId, CancellationToken ct = default)
    {
        await using var uow = await _uowFactory.CreateAsync(transactional: false, ct);
        var ticket = await uow.Tickets.GetByIdAsync(ticketId, ct);
        if (ticket is null) return null;
        var history = await uow.History.GetByTicketAsync(ticketId, includeInternal: true, ct);
        var evaluation = await uow.Evaluations.GetByTicketAsync(ticketId, ct);
        return new TicketDetail(ticket, history, evaluation);
    }

    public Task<Ticket> AssignAsync(int ticketId, byte[]? rowVersion, ITAdministrator admin, string? notes, CancellationToken ct = default) =>
        ExecuteAsync(ticketId, rowVersion, HistoryAction.Assigned, (t, _, now) => { t.Assign(admin, now, notes); return Task.CompletedTask; }, ct);

    public Task<Ticket> StartWorkAsync(int ticketId, byte[]? rowVersion, ITAdministrator admin, string? notes, CancellationToken ct = default) =>
        ExecuteAsync(ticketId, rowVersion, HistoryAction.StatusChanged, (t, _, now) => { t.StartWork(admin, now, notes); return Task.CompletedTask; }, ct);

    public Task<Ticket> PutOnHoldAsync(int ticketId, byte[]? rowVersion, ITAdministrator admin, string reason, CancellationToken ct = default) =>
        ExecuteAsync(ticketId, rowVersion, HistoryAction.StatusChanged, (t, _, now) => { t.PutOnHold(admin, now, reason); return Task.CompletedTask; }, ct);

    public Task<Ticket> EscalateAsync(int ticketId, byte[]? rowVersion, ITAdministrator admin, string reason, CancellationToken ct = default) =>
        ExecuteAsync(ticketId, rowVersion, HistoryAction.Escalated, (t, _, now) => { t.Escalate(admin, now, reason); return Task.CompletedTask; }, ct);

    public Task<Ticket> ReturnToQueueAsync(int ticketId, byte[]? rowVersion, ITAdministrator admin, string? notes, CancellationToken ct = default) =>
        ExecuteAsync(ticketId, rowVersion, HistoryAction.StatusChanged, (t, _, now) => { t.ReturnToQueue(admin, now, notes); return Task.CompletedTask; }, ct);

    public Task<Ticket> ResolveAsync(int ticketId, byte[]? rowVersion, ITAdministrator admin, string resolution, string rootCause, CancellationToken ct = default) =>
        ExecuteAsync(ticketId, rowVersion, HistoryAction.Resolved, async (t, uow, now) =>
        {
            t.Resolve(admin, now, resolution, rootCause);
            await uow.Tickets.UpdateAsync(t, ct);
            await PersistHistoryAsync(uow, t, ct);
            await _evaluationService.EvaluateAsync(uow, t.Id, null, null, ct);   // dispatch: score the delivery
        }, ct, persistedByMutation: true);

    public Task<Ticket> CloseAsync(int ticketId, byte[]? rowVersion, ITAdministrator admin, string? notes, CancellationToken ct = default) =>
        ExecuteAsync(ticketId, rowVersion, HistoryAction.Closed, (t, _, now) => { t.Close(admin, now, notes); return Task.CompletedTask; }, ct);

    public Task<Ticket> ReopenAsync(int ticketId, byte[]? rowVersion, ITAdministrator admin, string reason, CancellationToken ct = default) =>
        ExecuteAsync(ticketId, rowVersion, HistoryAction.Reopened, async (t, uow, now) =>
        {
            t.Reopen(admin, now, reason);
            await uow.Evaluations.DeleteByTicketAsync(t.Id, ct);   // stale until the next resolution
        }, ct);

    public Task<Ticket> ChangePriorityAsync(int ticketId, byte[]? rowVersion, ITAdministrator admin, TicketPriority priority, string? reason, CancellationToken ct = default) =>
        ExecuteAsync(ticketId, rowVersion, HistoryAction.PriorityChanged, async (t, uow, now) =>
        {
            var policy = await uow.SlaPolicies.GetAsync(priority, ct);
            t.ChangePriority(admin, policy, now, reason);
        }, ct);

    /// <summary>Notes only append to the audit log; the ticket row (and its rowversion) is left untouched.</summary>
    public Task<Ticket> AddNoteAsync(int ticketId, ITAdministrator admin, string note, bool isInternal, CancellationToken ct = default) =>
        ExecuteAsync(ticketId, null, HistoryAction.NoteAdded, async (t, uow, now) =>
        {
            t.AddNote(admin.Id, now, note, isInternal);
            await PersistHistoryAsync(uow, t, ct);
        }, ct, persistedByMutation: true);

    // =============================== Infrastructure ===============================

    /// <summary>
    /// Template for every admin action: load → concurrency check → mutate (domain) → update → audit → commit → notify.
    /// </summary>
    private async Task<Ticket> ExecuteAsync(int ticketId, byte[]? expectedRowVersion, HistoryAction action,
        Func<Ticket, IUnitOfWork, DateTime, Task> mutate, CancellationToken ct, bool persistedByMutation = false)
    {
        await using var uow = await _uowFactory.CreateAsync(transactional: true, ct);
        var ticket = await uow.Tickets.GetByIdAsync(ticketId, ct)
                     ?? throw new DomainException($"Ticket #{ticketId} no longer exists.");

        if (expectedRowVersion is { Length: > 0 } && !expectedRowVersion.AsSpan().SequenceEqual(ticket.RowVersion))
            throw new ConcurrencyException(ticket.TicketNumber);

        await mutate(ticket, uow, _clock.UtcNow);

        if (!persistedByMutation)
        {
            await uow.Tickets.UpdateAsync(ticket, ct);
            await PersistHistoryAsync(uow, ticket, ct);
        }

        await uow.CommitAsync(ct);
        OnTicketChanged(ticket, action);
        return ticket;
    }

    private static async Task PersistHistoryAsync(IUnitOfWork uow, Ticket ticket, CancellationToken ct)
    {
        foreach (var entry in ticket.DequeuePendingHistory())
            await uow.History.AddAsync(ticket.Id, entry, ct);
    }

    private static List<string> ValidateRequester(SubmitTicketRequest r)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(r.FullName)) errors.Add("Your full name is required.");
        else if (r.FullName.Trim().Length > 120) errors.Add("Full name must be at most 120 characters.");
        if (!ContactInfo.IsValidEmail(r.Email)) errors.Add("A valid e-mail address is required.");
        if (r.Phone?.Length > 30) errors.Add("Phone must be at most 30 characters.");
        if (r.Department?.Length > 80) errors.Add("Department must be at most 80 characters.");
        if (r.CategoryId <= 0) errors.Add("Please choose a category.");
        return errors;
    }

    private static string NormalizeNumber(string number)
    {
        var n = number.Trim().ToUpperInvariant();
        if (int.TryParse(n, out var raw)) return $"TKT-{raw:D7}";   // allow "42" → TKT-0000042
        return n;
    }

    private void OnTicketChanged(Ticket ticket, HistoryAction action) =>
        TicketChanged?.Invoke(this, new TicketChangedEventArgs(ticket.Id, ticket.TicketNumber, action, ticket.Status));
}

public sealed class TicketChangedEventArgs : EventArgs
{
    public TicketChangedEventArgs(int ticketId, string ticketNumber, HistoryAction action, TicketStatus status)
    {
        TicketId = ticketId;
        TicketNumber = ticketNumber;
        Action = action;
        Status = status;
    }

    public int TicketId { get; }
    public string TicketNumber { get; }
    public HistoryAction Action { get; }
    public TicketStatus Status { get; }
}
