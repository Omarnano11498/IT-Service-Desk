using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.Models;
using ServiceDesk.Domain;
using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Application.Services;

/// <summary>CSV (RFC 4180) and JSON exports of statistics and service evaluations.</summary>
public sealed class ReportExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep Arabic / accents readable in the file
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task ExportJsonAsync(StatisticsReport report, string path, CancellationToken ct = default)
    {
        var payload = new
        {
            report.GeneratedAtUtc,
            Period = new { report.FromUtc, report.ToUtc },
            Counts = new { report.Total, report.Open, report.InProgress, report.Pending, report.ResolvedOrClosed, report.OpenBreached, report.OpenAtRisk },
            report.ByStatus, report.ByCategory, report.ByPriority, report.ByAdministrator, report.ByGrade,
            ResolutionTimeHours = report.Resolution,
            FirstResponseTimeHours = report.FirstResponse,
            report.SlaCompliancePercent, report.ResponseCompliancePercent,
            report.MeanEvaluationScore, report.ServiceHealthIndex, report.ServiceHealthLabel,
            Evaluations = report.Evaluations.Select(ToRow)
        };
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, ct);
    }

    public async Task ExportCsvAsync(StatisticsReport report, string path, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Section,Metric,Value");
        void Row(string section, string metric, object value) =>
            sb.AppendLine(string.Join(",", Esc(section), Esc(metric), Esc(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")));

        Row("Period", "FromUtc", report.FromUtc.ToString("u"));
        Row("Period", "ToUtc", report.ToUtc.ToString("u"));
        Row("Counts", "Total", report.Total);
        Row("Counts", "Open", report.Open);
        Row("Counts", "ResolvedOrClosed", report.ResolvedOrClosed);
        Row("Counts", "OpenBreached", report.OpenBreached);
        Row("Resolution (h)", "Mean", report.Resolution.MeanHours);
        Row("Resolution (h)", "Median", report.Resolution.MedianHours);
        Row("Resolution (h)", "Min", report.Resolution.MinHours);
        Row("Resolution (h)", "Max", report.Resolution.MaxHours);
        Row("Resolution (h)", "P90", report.Resolution.P90Hours);
        Row("SLA", "ResolutionCompliance%", report.SlaCompliancePercent);
        Row("SLA", "ResponseCompliance%", report.ResponseCompliancePercent);
        Row("Evaluation", "MeanOverallScore", report.MeanEvaluationScore);
        Row("Evaluation", "ServiceHealthIndex", report.ServiceHealthIndex);
        foreach (var b in report.ByCategory) Row("ByCategory", b.Label, b.Count);
        foreach (var b in report.ByPriority) Row("ByPriority", b.Label, b.Count);
        foreach (var b in report.ByAdministrator) Row("ByAdministrator", b.Label, b.Count);

        sb.AppendLine();
        sb.AppendLine("TicketNumber,Priority,Administrator,ResolutionHours,SpeedScore,QualityScore,OverallScore,Grade,SlaBreached,Rating,EvaluatedAtUtc");
        foreach (var e in report.Evaluations)
        {
            sb.AppendLine(string.Join(",",
                Esc(e.TicketNumber), Esc(e.Priority.ToString()), Esc(e.AdminName ?? ""),
                Num(e.ResolutionHours), Num(e.SpeedScore), Num(e.QualityScore), Num(e.OverallScore),
                Esc(e.Grade.ToDisplay()), e.SlaBreached ? "1" : "0",
                e.SatisfactionRating?.ToString(CultureInfo.InvariantCulture) ?? "",
                e.EvaluatedAt.ToString("u", CultureInfo.InvariantCulture)));
        }

        // UTF-8 with BOM so Excel opens non-ASCII text correctly.
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
    }

    private static object ToRow(ServiceEvaluation e) => new
    {
        e.TicketNumber, e.Priority, e.AdminName, e.ResolutionHours, e.SpeedScore, e.QualityScore, e.OverallScore,
        Grade = e.Grade.ToDisplay(), e.SlaBreached, e.SatisfactionRating, e.RequesterComment, e.FormulaVersion, e.EvaluatedAt
    };

    /// <summary>RFC 4180 escaping + CSV-injection guard (=, +, -, @ prefixes).</summary>
    private static string Esc(string value)
    {
        if (value.Length > 0 && "=+-@".Contains(value[0]) && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static string Num(double? v) => v?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
    private static string Num(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>Admin-configurable SLA policy table.</summary>
public sealed class SlaPolicyService
{
    private readonly IUnitOfWorkFactory _uowFactory;

    public SlaPolicyService(IUnitOfWorkFactory uowFactory) => _uowFactory = uowFactory;

    public async Task<IReadOnlyList<SlaPolicy>> GetAllAsync(CancellationToken ct = default)
    {
        await using var uow = await _uowFactory.CreateAsync(transactional: false, ct);
        return await uow.SlaPolicies.GetAllAsync(ct);
    }

    /// <summary>Applies to tickets created from now on (existing deadlines stay contractual).</summary>
    public async Task SaveAsync(IEnumerable<(TicketPriority Priority, int ResponseMinutes, int ResolutionMinutes)> targets, CancellationToken ct = default)
    {
        await using var uow = await _uowFactory.CreateAsync(transactional: true, ct);
        foreach (var (priority, response, resolution) in targets)
        {
            var policy = await uow.SlaPolicies.GetAsync(priority, ct);
            policy.UpdateTargets(response, resolution);
            await uow.SlaPolicies.UpdateAsync(policy, ct);
        }
        await uow.CommitAsync(ct);
    }
}
