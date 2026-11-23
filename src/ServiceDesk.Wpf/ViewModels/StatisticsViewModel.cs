using System.Collections.ObjectModel;
using System.Windows.Input;
using ServiceDesk.Application.Models;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Wpf.Mvvm;
using ServiceDesk.Wpf.Services;

namespace ServiceDesk.Wpf.ViewModels;

/// <summary>Statistics &amp; service evaluation dashboard.</summary>
public sealed class StatisticsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private DateTime _fromDate = DateTime.Today.AddDays(-90);
    private DateTime _toDate = DateTime.Today;
    private StatisticsReport? _report;

    public StatisticsViewModel(AppServices services)
    {
        _services = services;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ExportCsvCommand = new AsyncRelayCommand(() => ExportAsync("csv"), () => Report is not null);
        ExportJsonCommand = new AsyncRelayCommand(() => ExportAsync("json"), () => Report is not null);
        RecalculateCommand = new AsyncRelayCommand(RecalculateAsync);
    }

    public override string Title => "Statistics & service evaluation";

    public ICommand RefreshCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand RecalculateCommand { get; }

    public DateTime FromDate { get => _fromDate; set => SetProperty(ref _fromDate, value); }
    public DateTime ToDate { get => _toDate; set => SetProperty(ref _toDate, value); }

    public StatisticsReport? Report
    {
        get => _report;
        private set
        {
            if (!SetProperty(ref _report, value)) return;
            OnPropertyChanged(nameof(HasReport));
            Replace(ByCategory, value?.ByCategory);
            Replace(ByPriority, value?.ByPriority);
            Replace(ByAdministrator, value?.ByAdministrator);
            Replace(ByStatus, value?.ByStatus);
            Replace(ByGrade, value?.ByGrade);
            Evaluations.Clear();
            if (value is not null) foreach (var e in value.Evaluations) Evaluations.Add(e);
        }
    }

    public bool HasReport => Report is not null;
    public ObservableCollection<BreakdownItem> ByCategory { get; } = new();
    public ObservableCollection<BreakdownItem> ByPriority { get; } = new();
    public ObservableCollection<BreakdownItem> ByAdministrator { get; } = new();
    public ObservableCollection<BreakdownItem> ByStatus { get; } = new();
    public ObservableCollection<BreakdownItem> ByGrade { get; } = new();
    public ObservableCollection<ServiceEvaluation> Evaluations { get; } = new();

    public string FormulaText =>
        "Speed = 100 if r ≤ 0.5; 100 − 80(r − 0.5) if r ≤ 1; max(0, 60 − 60(r − 1)) if breached   (r = actual / target resolution)\n" +
        "Quality = weighted mean of Response (0.4), Discipline (0.3) and Satisfaction (0.3, only when rated)\n" +
        "Overall = 0.6·Speed + 0.4·Quality  →  Excellent ≥ 85, Good ≥ 70, Fair ≥ 50, Poor < 50, SLA Breached overrides\n" +
        "Health = 0.5·SLA compliance + 0.3·mean Overall + 0.2·(100 − open-breach %)";

    public async Task RefreshAsync()
    {
        StatisticsReport? report = null;
        var from = DateTime.SpecifyKind(FromDate.Date, DateTimeKind.Local).ToUniversalTime();
        var to = DateTime.SpecifyKind(ToDate.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime();   // inclusive end date
        var ok = await RunAsync(async () => report = await _services.Statistics.ComputeAsync(from, to));
        if (ok && report is not null)
        {
            Report = null;   // force re-notification of all bound KPI properties
            Report = report;
            StatusMessage = $"Computed at {DateTime.Now:T} for {report.Total} tickets.";
        }
    }

    private async Task RecalculateAsync()
    {
        var count = 0;
        var ok = await RunAsync(async () => count = await _services.Evaluations.RecalculateAllAsync());
        if (ok)
        {
            await RefreshAsync();
            StatusMessage = $"Re-evaluated {count} resolved tickets with formula {_services.Evaluations.FormulaVersion}.";
        }
    }

    private async Task ExportAsync(string kind)
    {
        if (Report is null) return;
        var name = $"ServiceDesk_Report_{DateTime.Now:yyyyMMdd_HHmm}.{kind}";
        var filter = kind == "csv" ? "CSV file (*.csv)|*.csv" : "JSON file (*.json)|*.json";
        var path = _services.Dialogs.PickSaveFile(name, filter);
        if (path is null) return;

        var report = Report;
        await RunAsync(() => kind == "csv"
                ? _services.Export.ExportCsvAsync(report, path)
                : _services.Export.ExportJsonAsync(report, path),
            $"Exported to {path}");
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T>? source)
    {
        target.Clear();
        if (source is null) return;
        foreach (var item in source) target.Add(item);
    }
}
