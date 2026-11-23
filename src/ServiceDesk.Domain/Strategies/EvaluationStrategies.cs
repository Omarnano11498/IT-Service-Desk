namespace ServiceDesk.Domain.Strategies;

/// <summary>Facts needed to evaluate one resolved ticket (assembled by the DAL from Tickets + TicketHistory).</summary>
public sealed record EvaluationInput(
    int TicketId,
    DateTime CreatedAt,
    DateTime? FirstResponseAt,
    DateTime ResolvedAt,
    DateTime ResponseDueAt,
    DateTime ResolutionDueAt,
    int ReopenCount,
    int EscalationCount,
    int PendingCount,
    byte? SatisfactionRating);

public sealed record EvaluationResult(
    double SpeedScore,
    double ResponseScore,
    double DisciplineScore,
    double? SatisfactionScore,
    double QualityScore,
    double OverallScore,
    EvaluationGrade Grade,
    bool SlaBreached);

/// <summary>Strategy pattern: pluggable service-evaluation formula.</summary>
public interface IEvaluationStrategy
{
    string FormulaVersion { get; }
    EvaluationResult Evaluate(EvaluationInput input);
}

/// <summary>Tunable weights of the default formula (documented in docs/ARCHITECTURE.md §4).</summary>
public sealed record EvaluationWeights(
    double Speed = 0.6, double Quality = 0.4,
    double Response = 0.4, double Discipline = 0.3, double Satisfaction = 0.3,
    double ExcellentMin = 85, double GoodMin = 70, double FairMin = 50);

/// <summary>
/// Default formula v1.
///  r  = actual resolution / target resolution
///  Speed     = 100 (r ≤ 0.5) | 100 − 80·(r − 0.5) (0.5 &lt; r ≤ 1) | max(0, 60 − 60·(r − 1)) (r &gt; 1)
///  q  = time to first response / response target
///  Response  = 100 (q ≤ 1) | max(0, 100 − 50·(q − 1))
///  Discipline= max(0, 100 − 20·Reopens − 10·Escalations − 5·max(0, Pendings − 1))
///  Satisfaction = (rating − 1) / 4 · 100   (only when rated)
///  Quality   = weighted mean of the available components (weights re-normalised when unrated)
///  Overall   = 0.6·Speed + 0.4·Quality
///  Grade     = SLA Breached if resolved after deadline, else Excellent ≥85, Good ≥70, Fair ≥50, Poor.
/// </summary>
public sealed class WeightedSlaEvaluationStrategy : IEvaluationStrategy
{
    private readonly EvaluationWeights _w;

    public WeightedSlaEvaluationStrategy(EvaluationWeights? weights = null) => _w = weights ?? new EvaluationWeights();

    public string FormulaVersion => "v1";

    public EvaluationResult Evaluate(EvaluationInput i)
    {
        var resolutionTarget = Math.Max(1, (i.ResolutionDueAt - i.CreatedAt).TotalMinutes);
        var responseTarget = Math.Max(1, (i.ResponseDueAt - i.CreatedAt).TotalMinutes);
        var actualResolution = Math.Max(0, (i.ResolvedAt - i.CreatedAt).TotalMinutes);
        var actualResponse = Math.Max(0, ((i.FirstResponseAt ?? i.ResolvedAt) - i.CreatedAt).TotalMinutes);

        var speed = SpeedScore(actualResolution / resolutionTarget);
        var response = ResponseScore(actualResponse / responseTarget);
        var discipline = Math.Max(0, 100 - 20 * i.ReopenCount - 10 * i.EscalationCount - 5 * Math.Max(0, i.PendingCount - 1));
        double? satisfaction = i.SatisfactionRating is { } r ? (r - 1) / 4.0 * 100.0 : null;

        double quality;
        if (satisfaction is { } s)
        {
            var sum = _w.Response + _w.Discipline + _w.Satisfaction;
            quality = (_w.Response * response + _w.Discipline * discipline + _w.Satisfaction * s) / sum;
        }
        else
        {
            var sum = _w.Response + _w.Discipline;
            quality = (_w.Response * response + _w.Discipline * discipline) / sum;
        }

        var overall = Clamp((_w.Speed * speed + _w.Quality * quality) / (_w.Speed + _w.Quality));
        var breached = i.ResolvedAt > i.ResolutionDueAt;
        var grade = breached ? EvaluationGrade.SlaBreached
                  : overall >= _w.ExcellentMin ? EvaluationGrade.Excellent
                  : overall >= _w.GoodMin ? EvaluationGrade.Good
                  : overall >= _w.FairMin ? EvaluationGrade.Fair
                  : EvaluationGrade.Poor;

        return new EvaluationResult(speed, response, discipline, satisfaction, Clamp(quality), overall, grade, breached);
    }

    public static double SpeedScore(double r) =>
        r <= 0.5 ? 100
        : r <= 1.0 ? 100 - 80 * (r - 0.5)
        : Math.Max(0, 60 - 60 * (r - 1));

    public static double ResponseScore(double q) =>
        q <= 1.0 ? 100 : Math.Max(0, 100 - 50 * (q - 1));

    private static double Clamp(double v) => Math.Clamp(v, 0, 100);
}
