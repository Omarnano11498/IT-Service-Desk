using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.Models;
using ServiceDesk.Domain;
using ServiceDesk.Domain.Algorithms;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Rules;

namespace ServiceDesk.Application.Services;

/// <summary>
/// Single-pass aggregation (O(n) + O(n) expected for medians via Quickselect; P90 uses a sort, O(r log r)).
/// Service Health Index = 0.5·SLA compliance + 0.3·mean evaluation score + 0.2·(100 − open-breach %).
/// </summary>
public sealed class StatisticsService
{
    private readonly IUnitOfWorkFactory _uowFactory;
    private readonly IClock _clock;

    public StatisticsService(IUnitOfWorkFactory uowFactory, IClock clock)
    {
        _uowFactory = uowFactory;
        _clock = clock;
    }

    public async Task<StatisticsReport> ComputeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        if (toUtc <= fromUtc) throw new DomainException("The 'To' date must be after the 'From' date.");

        IReadOnlyList<Ticket> tickets;
        IReadOnlyList<ServiceEvaluation> evaluations;
        await using (var uow = await _uowFactory.CreateAsync(transactional: false, ct))
        {
            tickets = await uow.Tickets.GetCreatedBetweenAsync(fromUtc, toUtc, ct);
            evaluations = await uow.Evaluations.GetForTicketsCreatedBetweenAsync(fromUtc, toUtc, ct);
        }

        return Compute(tickets, evaluations, fromUtc, toUtc, _clock.UtcNow);
    }

    /// <summary>Pure function (unit-testable): no I/O.</summary>
    public static StatisticsReport Compute(IReadOnlyList<Ticket> tickets, IReadOnlyList<ServiceEvaluation> evaluations,
        DateTime fromUtc, DateTime toUtc, DateTime nowUtc)
    {
        var total = tickets.Count;
        int open = 0, inProgress = 0, pending = 0, resolved = 0, openBreached = 0, openAtRisk = 0;
        int resolvedWithinSla = 0, responded = 0, respondedWithinSla = 0;
        var resolutionHours = new List<double>();
        var responseHours = new List<double>();
        var byStatus = new Dictionary<string, int>();
        var byCategory = new Dictionary<string, int>();
        var byPriority = new Dictionary<string, int>();
        var byAdmin = new Dictionary<string, int>();

        foreach (var t in tickets)   // single pass: O(n)
        {
            Increment(byStatus, t.Status.ToDisplay());
            Increment(byCategory, t.CategoryName);
            Increment(byPriority, t.Priority.ToString());
            Increment(byAdmin, t.AssignedAdminName ?? "(unassigned)");

            if (t.IsOpen)
            {
                open++;
                if (t.Status == TicketStatus.InProgress) inProgress++;
                if (t.Status == TicketStatus.Pending) pending++;
                var sla = SlaMonitor.Evaluate(t, nowUtc);
                if (sla == SlaState.Breached) openBreached++;
                else if (sla == SlaState.AtRisk) openAtRisk++;
            }
            else
            {
                resolved++;
            }

            if (t.ResolvedAt is { } ra)
            {
                resolutionHours.Add((ra - t.CreatedAt).TotalHours);
                if (ra <= t.ResolutionDueAt) resolvedWithinSla++;
            }

            if (t.FirstResponseAt is { } fr)
            {
                responded++;
                responseHours.Add((fr - t.CreatedAt).TotalHours);
                if (fr <= t.ResponseDueAt) respondedWithinSla++;
            }
        }

        var slaCompliance = resolutionHours.Count == 0 ? 0 : 100.0 * resolvedWithinSla / resolutionHours.Count;
        var responseCompliance = responded == 0 ? 0 : 100.0 * respondedWithinSla / responded;
        var meanScore = evaluations.Count == 0 ? 0 : (double)evaluations.Average(e => e.OverallScore);
        var openBreachPct = open == 0 ? 0 : 100.0 * openBreached / open;

        double health;
        if (resolutionHours.Count == 0 && evaluations.Count == 0) health = open == 0 ? 0 : 100 - openBreachPct;
        else health = 0.5 * slaCompliance + 0.3 * meanScore + 0.2 * (100 - openBreachPct);

        var byGrade = evaluations
            .GroupBy(e => e.Grade)
            .OrderBy(g => g.Key)
            .Select(g => new BreakdownItem(g.Key.ToDisplay(), g.Count(), Pct(g.Count(), evaluations.Count)))
            .ToList();

        return new StatisticsReport
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            GeneratedAtUtc = nowUtc,
            Total = total,
            Open = open,
            InProgress = inProgress,
            Pending = pending,
            ResolvedOrClosed = resolved,
            OpenBreached = openBreached,
            OpenAtRisk = openAtRisk,
            ByStatus = ToBreakdown(byStatus, total),
            ByCategory = ToBreakdown(byCategory, total),
            ByPriority = ToBreakdown(byPriority, total),
            ByAdministrator = ToBreakdown(byAdmin, total),
            ByGrade = byGrade,
            Resolution = Describe(resolutionHours),
            FirstResponse = Describe(responseHours),
            SlaCompliancePercent = Math.Round(slaCompliance, 1),
            ResponseCompliancePercent = Math.Round(responseCompliance, 1),
            MeanEvaluationScore = Math.Round(meanScore, 1),
            ServiceHealthIndex = Math.Round(health, 1),
            ServiceHealthLabel = health >= 85 ? "Healthy" : health >= 70 ? "Stable" : health >= 50 ? "Needs attention" : "Critical",
            Evaluations = evaluations
        };
    }

    /// <summary>Mean, median (Quickselect), min, max, population std-dev, 90th percentile (nearest-rank).</summary>
    public static DurationStats Describe(IReadOnlyList<double> hours)
    {
        var n = hours.Count;
        if (n == 0) return DurationStats.Empty;

        double sum = 0, min = double.MaxValue, max = double.MinValue;
        foreach (var h in hours) { sum += h; if (h < min) min = h; if (h > max) max = h; }
        var mean = sum / n;

        double sq = 0;
        foreach (var h in hours) sq += (h - mean) * (h - mean);
        var std = Math.Sqrt(sq / n);

        var median = QuickSelect.Median(hours);
        var copy = hours.ToArray();
        var p90 = QuickSelect.Select(copy, Math.Max(0, (int)Math.Ceiling(0.9 * n) - 1));

        return new DurationStats(n, R(mean), R(median), R(min), R(max), R(std), R(p90));
    }

    private static void Increment(Dictionary<string, int> map, string key) =>
        map[key] = map.TryGetValue(key, out var c) ? c + 1 : 1;

    private static IReadOnlyList<BreakdownItem> ToBreakdown(Dictionary<string, int> map, int total) =>
        map.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
           .Select(kv => new BreakdownItem(kv.Key, kv.Value, Pct(kv.Value, total)))
           .ToList();

    private static double Pct(int part, int total) => total == 0 ? 0 : Math.Round(100.0 * part / total, 1);
    private static double R(double v) => Math.Round(v, 2);
}
