using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Rules;

namespace ServiceDesk.Domain.Strategies;

/// <summary>
/// Strategy pattern for queue ordering. A comparer is created for a fixed "now" snapshot,
/// which keeps the comparison a strict weak ordering during one sort (time-dependent scores
/// must not drift mid-sort, or Merge Sort / heap invariants would be violated).
/// Every strategy ends with CreatedAt then Id as tie-breakers → total, deterministic order.
/// </summary>
public interface ITicketOrderingStrategy
{
    string Name { get; }
    string Description { get; }
    IComparer<Ticket> CreateComparer(DateTime nowUtc);
}

internal static class TieBreak
{
    public static int Apply(Ticket x, Ticket y)
    {
        var c = x.CreatedAt.CompareTo(y.CreatedAt);       // older first (queue aging)
        return c != 0 ? c : x.Id.CompareTo(y.Id);
    }
}

/// <summary>
/// Default. Composite urgency score (higher = served first):
///   Score = P + 0.6·S + A
///   P = priority weight (Critical 100, High 70, Medium 40, Low 10)
///   S = min(elapsed SLA fraction, 1.5) · 100          (0 … 150, &gt;100 means breached)
///   A = min(age hours, 72) / 72 · 20                   (0 … 20, anti-starvation aging)
/// A badly breached Low ticket can therefore overtake a fresh Critical one, preventing starvation.
/// </summary>
public sealed class WeightedUrgencyStrategy : ITicketOrderingStrategy
{
    public string Name => "Weighted urgency (priority + SLA pressure + aging)";
    public string Description => "Score = P + 0.6·S + A. Balances priority, SLA consumption and waiting time.";

    public static double Score(Ticket t, DateTime nowUtc)
    {
        double p = t.Priority switch
        {
            TicketPriority.Critical => 100,
            TicketPriority.High => 70,
            TicketPriority.Medium => 40,
            _ => 10
        };
        var s = Math.Min(SlaMonitor.ElapsedFraction(t, nowUtc), 1.5) * 100.0;
        var a = Math.Min(t.Age(nowUtc).TotalHours, 72.0) / 72.0 * 20.0;
        return p + 0.6 * s + a;
    }

    public IComparer<Ticket> CreateComparer(DateTime nowUtc) => Comparer<Ticket>.Create((x, y) =>
    {
        if (ReferenceEquals(x, y)) return 0;
        var c = Score(y, nowUtc).CompareTo(Score(x, nowUtc));   // descending score
        return c != 0 ? c : TieBreak.Apply(x, y);
    });
}

/// <summary>Lexicographic: Priority → SLA remaining time → age → id.</summary>
public sealed class StrictPriorityStrategy : ITicketOrderingStrategy
{
    public string Name => "Strict priority (Critical first)";
    public string Description => "Priority level first, then nearest resolution deadline, then oldest.";

    public IComparer<Ticket> CreateComparer(DateTime nowUtc) => Comparer<Ticket>.Create((x, y) =>
    {
        if (ReferenceEquals(x, y)) return 0;
        var c = ((byte)x.Priority).CompareTo((byte)y.Priority);
        if (c != 0) return c;
        c = x.ResolutionDueAt.CompareTo(y.ResolutionDueAt);
        return c != 0 ? c : TieBreak.Apply(x, y);
    });
}

/// <summary>Earliest Deadline First (EDF) – optimal for minimising maximum lateness on one server.</summary>
public sealed class EarliestDeadlineFirstStrategy : ITicketOrderingStrategy
{
    public string Name => "Earliest SLA deadline first (EDF)";
    public string Description => "Nearest resolution deadline first; minimises the worst SLA lateness.";

    public IComparer<Ticket> CreateComparer(DateTime nowUtc) => Comparer<Ticket>.Create((x, y) =>
    {
        if (ReferenceEquals(x, y)) return 0;
        var c = x.ResolutionDueAt.CompareTo(y.ResolutionDueAt);
        if (c != 0) return c;
        c = ((byte)x.Priority).CompareTo((byte)y.Priority);
        return c != 0 ? c : TieBreak.Apply(x, y);
    });
}

/// <summary>First-In-First-Out – fairness baseline.</summary>
public sealed class FifoStrategy : ITicketOrderingStrategy
{
    public string Name => "First in, first out (FIFO)";
    public string Description => "Submission order only. Useful as a fairness baseline.";

    public IComparer<Ticket> CreateComparer(DateTime nowUtc) =>
        Comparer<Ticket>.Create((x, y) => ReferenceEquals(x, y) ? 0 : TieBreak.Apply(x, y));
}
