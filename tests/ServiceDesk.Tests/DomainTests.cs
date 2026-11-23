using ServiceDesk.Application.Services;
using ServiceDesk.Domain;
using ServiceDesk.Domain.Algorithms;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Factories;
using ServiceDesk.Domain.Rules;
using ServiceDesk.Domain.Strategies;
using Xunit;

namespace ServiceDesk.Tests;

public class PriorityAndLifecycleTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TicketFactory Factory = new();

    internal static Ticket NewTicket(Urgency u, Impact i, DateTime createdAt, TicketCategoryType type = TicketCategoryType.General)
    {
        var p = PriorityMatrix.Calculate(u, i);
        var policy = p switch
        {
            TicketPriority.Critical => SlaPolicy.Create(p, 15, 240),
            TicketPriority.High => SlaPolicy.Create(p, 30, 480),
            TicketPriority.Medium => SlaPolicy.Create(p, 120, 1440),
            _ => SlaPolicy.Create(p, 480, 4320)
        };
        var category = new TicketCategory { Id = 1, Name = type.ToString(), CategoryType = type };
        var spec = new NewTicketSpec(u, i, "Subject", "Description",
            DeviceType: "Laptop", ApplicationName: "CRM", NetworkLocation: "Floor 3");
        return Factory.CreateNew(requesterId: 10, category, policy, spec, createdAt);
    }

    internal static ITAdministrator Admin(int id = 1) => new() { Id = id, FullName = $"Admin {id}", UserName = $"admin{id}", IsActive = true };

    [Theory]
    [InlineData(Urgency.High, Impact.High, TicketPriority.Critical)]
    [InlineData(Urgency.High, Impact.Medium, TicketPriority.High)]
    [InlineData(Urgency.Medium, Impact.High, TicketPriority.High)]
    [InlineData(Urgency.High, Impact.Low, TicketPriority.Medium)]
    [InlineData(Urgency.Medium, Impact.Medium, TicketPriority.Medium)]
    [InlineData(Urgency.Medium, Impact.Low, TicketPriority.Low)]
    [InlineData(Urgency.Low, Impact.Low, TicketPriority.Low)]
    public void Priority_matrix_follows_ITIL(Urgency u, Impact i, TicketPriority expected) =>
        Assert.Equal(expected, PriorityMatrix.Calculate(u, i));

    [Theory]
    [InlineData(TicketCategoryType.Hardware, typeof(HardwareTicket))]
    [InlineData(TicketCategoryType.Software, typeof(SoftwareTicket))]
    [InlineData(TicketCategoryType.Network, typeof(NetworkTicket))]
    [InlineData(TicketCategoryType.General, typeof(GeneralTicket))]
    public void Factory_creates_specialised_ticket(TicketCategoryType type, Type expected) =>
        Assert.IsType(expected, NewTicket(Urgency.Medium, Impact.Medium, T0, type));

    [Fact]
    public void New_ticket_is_queued_with_sla_deadlines_and_audit_entry()
    {
        var t = NewTicket(Urgency.High, Impact.High, T0);
        Assert.Equal(TicketStatus.Queued, t.Status);
        Assert.Equal(T0.AddMinutes(15), t.ResponseDueAt);
        Assert.Equal(T0.AddMinutes(240), t.ResolutionDueAt);
        Assert.Single(t.PendingHistory, h => h.Action == HistoryAction.Created);
    }

    [Fact]
    public void Full_lifecycle_records_first_response_resolution_and_history()
    {
        var t = NewTicket(Urgency.Medium, Impact.Medium, T0);
        var admin = Admin();
        t.Assign(admin, T0.AddMinutes(10));
        t.StartWork(admin, T0.AddMinutes(12));
        t.Resolve(admin, T0.AddHours(3), "Fixed", "Config drift");
        t.Close(admin, T0.AddHours(4));

        Assert.Equal(TicketStatus.Closed, t.Status);
        Assert.Equal(T0.AddMinutes(10), t.FirstResponseAt);
        Assert.Equal(TimeSpan.FromHours(3), t.ResolutionDuration);
        Assert.Equal(5, t.PendingHistory.Count);   // created, assigned, started, resolved, closed
    }

    [Fact]
    public void Invalid_transition_is_rejected()
    {
        var t = NewTicket(Urgency.Low, Impact.Low, T0);
        Assert.Throws<DomainException>(() => t.Close(Admin(), T0.AddMinutes(1)));
        Assert.Throws<DomainException>(() => t.Resolve(Admin(), T0.AddMinutes(1), "", "x"));
    }

    [Fact]
    public void Software_ticket_requires_application_name()
    {
        var category = new TicketCategory { Id = 3, Name = "Software", CategoryType = TicketCategoryType.Software };
        var spec = new NewTicketSpec(Urgency.Low, Impact.Low, "S", "D");
        var ex = Assert.Throws<ValidationException>(() =>
            Factory.CreateNew(1, category, SlaPolicy.Create(TicketPriority.Low, 480, 4320), spec, T0));
        Assert.Contains(ex.Errors, e => e.Contains("Application name"));
    }
}

public class AlgorithmTests
{
    [Fact]
    public void MergeSort_is_correct_and_stable()
    {
        var rnd = new Random(42);
        var data = Enumerable.Range(0, 500).Select(i => (Key: rnd.Next(0, 20), Seq: i)).ToList();
        var sorted = MergeSort.Sort(data, Comparer<(int Key, int Seq)>.Create((a, b) => a.Key.CompareTo(b.Key)));

        for (var i = 1; i < sorted.Length; i++)
        {
            Assert.True(sorted[i - 1].Key <= sorted[i].Key);
            if (sorted[i - 1].Key == sorted[i].Key) Assert.True(sorted[i - 1].Seq < sorted[i].Seq);   // stability
        }
    }

    [Fact]
    public void Heap_dequeues_in_comparer_order()
    {
        var values = new[] { 9, 3, 7, 1, 8, 2, 6, 5, 4, 0 };
        var heap = new BinaryHeapPriorityQueue<int>(Comparer<int>.Default, values);
        heap.Enqueue(-1);
        var output = new List<int>();
        while (heap.TryDequeue(out var v)) output.Add(v);
        Assert.Equal(Enumerable.Range(-1, 11), output);
    }

    [Theory]
    [InlineData(new double[] { 5 }, 5)]
    [InlineData(new double[] { 3, 1, 2 }, 2)]
    [InlineData(new double[] { 4, 1, 3, 2 }, 2.5)]
    [InlineData(new double[] { 7, 7, 7, 1 }, 7)]
    public void QuickSelect_median(double[] values, double expected) =>
        Assert.Equal(expected, QuickSelect.Median(values), 6);

    [Fact]
    public void Weighted_strategy_lets_breached_low_overtake_fresh_critical()
    {
        var now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        var freshCritical = PriorityAndLifecycleTests.NewTicket(Urgency.High, Impact.High, now.AddMinutes(-1));
        var breachedLow = PriorityAndLifecycleTests.NewTicket(Urgency.Low, Impact.Low, now.AddDays(-5));   // 120 h > 72 h target
        freshCritical.Id = 1; breachedLow.Id = 2;

        var ordered = MergeSort.Sort(new[] { freshCritical, breachedLow }, new WeightedUrgencyStrategy().CreateComparer(now));
        Assert.Same(breachedLow, ordered[0]);

        var strict = MergeSort.Sort(new[] { breachedLow, freshCritical }, new StrictPriorityStrategy().CreateComparer(now));
        Assert.Same(freshCritical, strict[0]);
    }
}

public class EvaluationAndStatisticsTests
{
    private static readonly DateTime T0 = new(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0.25, 100)]
    [InlineData(0.5, 100)]
    [InlineData(1.0, 60)]
    [InlineData(1.5, 30)]
    [InlineData(2.5, 0)]
    public void Speed_score_curve(double ratio, double expected) =>
        Assert.Equal(expected, WeightedSlaEvaluationStrategy.SpeedScore(ratio), 6);

    [Fact]
    public void Fast_clean_resolution_is_excellent()
    {
        var input = new EvaluationInput(1, T0, T0.AddMinutes(5), T0.AddHours(1), T0.AddMinutes(30), T0.AddHours(8), 0, 0, 0, 5);
        var r = new WeightedSlaEvaluationStrategy().Evaluate(input);
        Assert.Equal(100, r.SpeedScore, 6);
        Assert.Equal(100, r.QualityScore, 6);
        Assert.Equal(EvaluationGrade.Excellent, r.Grade);
        Assert.False(r.SlaBreached);
    }

    [Fact]
    public void Late_resolution_is_flagged_breached()
    {
        var input = new EvaluationInput(1, T0, T0.AddHours(2), T0.AddHours(10), T0.AddMinutes(30), T0.AddHours(8), 1, 1, 2, null);
        var r = new WeightedSlaEvaluationStrategy().Evaluate(input);
        Assert.True(r.SlaBreached);
        Assert.Equal(EvaluationGrade.SlaBreached, r.Grade);
        Assert.Equal(65, r.DisciplineScore, 6);   // 100 − 20·1 − 10·1 − 5·(2−1)
    }

    [Fact]
    public void Statistics_compute_counts_and_resolution_times()
    {
        var admin = PriorityAndLifecycleTests.Admin();
        var tickets = new List<Ticket>();
        double[] hours = { 1, 2, 3, 10 };
        foreach (var h in hours)
        {
            var t = PriorityAndLifecycleTests.NewTicket(Urgency.High, Impact.Medium, T0);   // High: 8 h target
            t.Assign(admin, T0.AddMinutes(10));
            t.StartWork(admin, T0.AddMinutes(11));
            t.Resolve(admin, T0.AddHours(h), "done", "cause");
            tickets.Add(t);
        }
        tickets.Add(PriorityAndLifecycleTests.NewTicket(Urgency.Low, Impact.Low, T0));   // still open

        var report = StatisticsService.Compute(tickets, Array.Empty<ServiceEvaluation>(), T0.AddDays(-1), T0.AddDays(1), T0.AddHours(12));

        Assert.Equal(5, report.Total);
        Assert.Equal(1, report.Open);
        Assert.Equal(4, report.ResolvedOrClosed);
        Assert.Equal(4, report.Resolution.MeanHours, 2);
        Assert.Equal(2.5, report.Resolution.MedianHours, 2);
        Assert.Equal(1, report.Resolution.MinHours, 2);
        Assert.Equal(10, report.Resolution.MaxHours, 2);
        Assert.Equal(75, report.SlaCompliancePercent, 1);   // 3 of 4 within 8 h
    }
}
