using System.Diagnostics;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.Models;
using ServiceDesk.Domain;
using ServiceDesk.Domain.Algorithms;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Rules;
using ServiceDesk.Domain.Strategies;

namespace ServiceDesk.Application.Services;

/// <summary>
/// Deterministic, priority-aware ticket queue.
/// - Display order: filtered set from SQL → stable Merge Sort with the selected strategy (Θ(n log n)).
/// - "Receive next": binary heap built in O(n), each dequeue O(log n).
/// </summary>
public sealed class QueueService
{
    private readonly IUnitOfWorkFactory _uowFactory;
    private readonly TicketService _ticketService;
    private readonly IClock _clock;

    public QueueService(IUnitOfWorkFactory uowFactory, TicketService ticketService, IClock clock,
        IEnumerable<ITicketOrderingStrategy> strategies)
    {
        _uowFactory = uowFactory;
        _ticketService = ticketService;
        _clock = clock;
        Strategies = strategies.ToList();
        if (Strategies.Count == 0) throw new ArgumentException("At least one ordering strategy is required.", nameof(strategies));
    }

    public IReadOnlyList<ITicketOrderingStrategy> Strategies { get; }

    public async Task<QueueSnapshot> LoadAsync(TicketFilter filter, ITicketOrderingStrategy strategy, CancellationToken ct = default)
    {
        IReadOnlyList<Ticket> tickets;
        await using (var uow = await _uowFactory.CreateAsync(transactional: false, ct))
            tickets = await uow.Tickets.SearchAsync(filter, ct);

        var now = _clock.UtcNow;
        var sw = Stopwatch.StartNew();
        var ordered = MergeSort.Sort(tickets, strategy.CreateComparer(now));
        sw.Stop();

        var items = new List<QueueItem>(ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
        {
            var t = ordered[i];
            items.Add(new QueueItem(
                Rank: i + 1,
                Ticket: t,
                UrgencyScore: Math.Round(WeightedUrgencyStrategy.Score(t, now), 1),
                SlaState: SlaMonitor.Evaluate(t, now),
                ResponseOverdue: SlaMonitor.IsResponseOverdue(t, now),
                RemainingResolution: SlaMonitor.RemainingResolutionTime(t, now),
                Age: t.Age(now)));
        }
        return new QueueSnapshot(items, now, strategy.Name, sw.Elapsed);
    }

    /// <summary>
    /// Pops the highest-priority unclaimed (Queued) ticket from a heap and assigns it to <paramref name="admin"/>.
    /// If another admin wins the race (concurrency conflict), the next heap element is tried.
    /// </summary>
    public async Task<Ticket?> ReceiveNextAsync(ITAdministrator admin, ITicketOrderingStrategy strategy, CancellationToken ct = default)
    {
        IReadOnlyList<Ticket> queued;
        await using (var uow = await _uowFactory.CreateAsync(transactional: false, ct))
            queued = await uow.Tickets.SearchAsync(new TicketFilter { Status = TicketStatus.Queued, OpenOnly = true }, ct);

        var heap = new BinaryHeapPriorityQueue<Ticket>(strategy.CreateComparer(_clock.UtcNow), queued);
        while (heap.TryDequeue(out var candidate))
        {
            try
            {
                return await _ticketService.AssignAsync(candidate.Id, candidate.RowVersion, admin, "Received from queue (next in order)", ct);
            }
            catch (ConcurrencyException)
            {
                // claimed by a colleague in the meantime → try the next one
            }
            catch (DomainException)
            {
                // e.g. status changed to a non-assignable state → skip
            }
        }
        return null;
    }
}
