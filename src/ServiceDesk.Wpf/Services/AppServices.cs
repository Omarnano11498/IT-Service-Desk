using System.Windows;
using Microsoft.Win32;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.Services;
using ServiceDesk.Data.Configuration;

namespace ServiceDesk.Wpf.Services;

/// <summary>UI-only abstraction so ViewModels never touch MessageBox / file dialogs directly (testable).</summary>
public interface IDialogService
{
    void ShowInfo(string message, string title = "IT Service Desk");
    void ShowError(string message, string title = "IT Service Desk");
    bool Confirm(string message, string title = "Please confirm");
    string? PickSaveFile(string defaultFileName, string filter);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => System.Windows.Application.Current?.MainWindow;

    public void ShowInfo(string message, string title = "IT Service Desk") =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string message, string title = "IT Service Desk") =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title = "Please confirm") =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        Owner is { } owner
            ? MessageBox.Show(owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);

    public string? PickSaveFile(string defaultFileName, string filter)
    {
        var dlg = new SaveFileDialog { FileName = defaultFileName, Filter = filter, AddExtension = true, OverwritePrompt = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }
}

/// <summary>Composition-root output: every service the ViewModels need (manual DI, no container required).</summary>
public sealed record AppServices(
    AppConfigurationManager Configuration,
    IClock Clock,
    TicketService Tickets,
    QueueService Queue,
    AuthService Auth,
    StatisticsService Statistics,
    EvaluationService Evaluations,
    SlaPolicyService SlaPolicies,
    ReportExportService Export,
    IDialogService Dialogs);
