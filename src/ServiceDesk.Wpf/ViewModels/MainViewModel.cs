using System.Windows.Input;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Wpf.Mvvm;
using ServiceDesk.Wpf.Services;

namespace ServiceDesk.Wpf.ViewModels;

/// <summary>
/// Shell: role/mode switcher + navigation. End-user pages are always available; admin pages exist
/// only while an authenticated <see cref="ITAdministrator"/> is present (access control by construction).
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly SubmitTicketViewModel _submit;
    private readonly TrackTicketViewModel _track;

    private ViewModelBase _currentPage;
    private ITAdministrator? _admin;
    private AdminQueueViewModel? _queue;
    private StatisticsViewModel? _statistics;
    private string _connectionTarget = "";

    public MainViewModel(AppServices services)
    {
        _services = services;
        _submit = new SubmitTicketViewModel(services);
        _track = new TrackTicketViewModel(services);
        _currentPage = _submit;

        ShowSubmitCommand = new RelayCommand(() => CurrentPage = _submit);
        ShowTrackCommand = new RelayCommand(() => CurrentPage = _track);
        ShowAdminCommand = new RelayCommand(ShowAdmin);
        ShowQueueCommand = new RelayCommand(() => { if (_queue is not null) CurrentPage = _queue; }, () => IsAdmin);
        ShowStatisticsCommand = new AsyncRelayCommand(ShowStatisticsAsync, () => IsAdmin);
        ShowSettingsCommand = new AsyncRelayCommand(ShowSettingsAsync, () => CanOpenSettings);
        LogoutCommand = new RelayCommand(Logout, () => IsAdmin);

        services.Configuration.SettingsChanged += (_, _) => RefreshConnectionTarget();
        RefreshConnectionTarget();
    }

    public ICommand ShowSubmitCommand { get; }
    public ICommand ShowTrackCommand { get; }
    public ICommand ShowAdminCommand { get; }
    public ICommand ShowQueueCommand { get; }
    public ICommand ShowStatisticsCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand LogoutCommand { get; }

    public ViewModelBase CurrentPage { get => _currentPage; private set => SetProperty(ref _currentPage, value); }

    public ITAdministrator? Admin
    {
        get => _admin;
        private set
        {
            if (!SetProperty(ref _admin, value)) return;
            OnPropertyChanged(nameof(IsAdmin));
            OnPropertyChanged(nameof(ModeText));
            OnPropertyChanged(nameof(CanOpenSettings));
        }
    }

    public bool IsAdmin => Admin is not null;
    public string ModeText => IsAdmin ? $"IT Administrator: {Admin!.FullName}" : "End User mode (no sign-in required)";

    /// <summary>First-run bootstrap: settings are open until a connection is saved; afterwards admin-only.</summary>
    public bool CanOpenSettings => IsAdmin || !_services.Configuration.HasSavedSettings;

    public string ConnectionTarget { get => _connectionTarget; private set => SetProperty(ref _connectionTarget, value); }

    /// <summary>Called once the window is shown.</summary>
    public async Task InitializeAsync()
    {
        if (!_services.Configuration.HasSavedSettings)
        {
            await ShowSettingsAsync();
            return;
        }
        _submit.LoadCategoriesCommand.Execute(null);
    }

    private void ShowAdmin()
    {
        if (IsAdmin && _queue is not null) { CurrentPage = _queue; return; }
        CurrentPage = new AdminLoginViewModel(_services, OnLoggedIn);
    }

    private async void OnLoggedIn(ITAdministrator admin)
    {
        Admin = admin;
        _queue = new AdminQueueViewModel(_services, admin);
        _statistics = new StatisticsViewModel(_services);
        CurrentPage = _queue;
        await _queue.InitializeAsync();
    }

    private async Task ShowStatisticsAsync()
    {
        if (_statistics is null) return;
        CurrentPage = _statistics;
        if (!_statistics.HasReport) await _statistics.RefreshAsync();
    }

    private async Task ShowSettingsAsync()
    {
        var settings = new SettingsViewModel(_services, Admin);
        settings.ConnectionApplied += (_, _) =>
        {
            OnPropertyChanged(nameof(CanOpenSettings));
            _submit.LoadCategoriesCommand.Execute(null);
        };
        CurrentPage = settings;
        await settings.InitializeAsync();
    }

    private void Logout()
    {
        _queue?.Dispose();
        _queue = null;
        _statistics = null;
        Admin = null;
        CurrentPage = _submit;
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshConnectionTarget()
    {
        try
        {
            var cfg = _services.Configuration;
            ConnectionTarget = cfg.HasSavedSettings
                ? $"SQL Server: {cfg.Current.DescribeTarget()}"
                : "SQL Server: not configured — open Connection Settings";
        }
        catch (Exception ex)
        {
            ConnectionTarget = "SQL Server: invalid settings — " + ex.Message;
        }
    }
}
