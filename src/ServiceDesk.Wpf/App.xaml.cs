using System.Windows;
using System.Windows.Threading;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.Security;
using ServiceDesk.Application.Services;
using ServiceDesk.Data;
using ServiceDesk.Data.Configuration;
using ServiceDesk.Data.Connection;
using ServiceDesk.Domain.Factories;
using ServiceDesk.Domain.Strategies;
using ServiceDesk.Wpf.Services;
using ServiceDesk.Wpf.ViewModels;

namespace ServiceDesk.Wpf;

/// <summary>
/// Composition root (manual dependency injection). Note: the base class is written fully qualified because
/// the sibling namespace "ServiceDesk.Application" would otherwise shadow System.Windows.Application.
/// </summary>
public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // ---- Infrastructure ----
        var configuration = AppConfigurationManager.Instance;                 // Singleton
        IClock clock = new SystemClock();
        ITicketFactory ticketFactory = new TicketFactory();                   // Factory Method
        var uowFactory = new SqlUnitOfWorkFactory(new SqlConnectionFactory(configuration), ticketFactory);  // UoW + Repositories

        // ---- Application services ----
        var evaluation = new EvaluationService(uowFactory, new WeightedSlaEvaluationStrategy(), clock);     // Strategy
        var tickets = new TicketService(uowFactory, ticketFactory, evaluation, clock);
        var queue = new QueueService(uowFactory, tickets, clock, new ITicketOrderingStrategy[]
        {
            new WeightedUrgencyStrategy(),          // Strategy (default)
            new StrictPriorityStrategy(),
            new EarliestDeadlineFirstStrategy(),
            new FifoStrategy()
        });

        var services = new AppServices(
            configuration, clock, tickets, queue,
            new AuthService(uowFactory, new Pbkdf2PasswordHasher(), clock),
            new StatisticsService(uowFactory, clock),
            evaluation,
            new SlaPolicyService(uowFactory),
            new ReportExportService(),
            new DialogService());

        var shell = new MainViewModel(services);
        var window = new MainWindow { DataContext = shell };
        MainWindow = window;
        window.Show();
        await shell.InitializeAsync();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"An unexpected error occurred:\n\n{e.Exception.Message}", "IT Service Desk",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;   // keep the desktop app alive; the failed operation is abandoned
    }
}
