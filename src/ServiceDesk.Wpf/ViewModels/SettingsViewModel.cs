using System.Collections.ObjectModel;
using System.Windows.Input;
using ServiceDesk.Application.Security;
using ServiceDesk.Data.Configuration;
using ServiceDesk.Data.Connection;
using ServiceDesk.Domain;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Wpf.Mvvm;
using ServiceDesk.Wpf.Services;

namespace ServiceDesk.Wpf.ViewModels;

/// <summary>Editable SLA row.</summary>
public sealed class SlaRowViewModel : ObservableObject
{
    private int _response, _resolution;

    public SlaRowViewModel(SlaPolicy p)
    {
        Priority = p.Priority;
        _response = p.ResponseTargetMinutes;
        _resolution = p.ResolutionTargetMinutes;
    }

    public TicketPriority Priority { get; }
    public int ResponseMinutes { get => _response; set { if (SetProperty(ref _response, value)) OnPropertyChanged(nameof(Summary)); } }
    public int ResolutionMinutes { get => _resolution; set { if (SetProperty(ref _resolution, value)) OnPropertyChanged(nameof(Summary)); } }
    public string Summary => $"respond ≤ {Format(ResponseMinutes)}, resolve ≤ {Format(ResolutionMinutes)}";

    private static string Format(int minutes) =>
        minutes >= 1440 && minutes % 1440 == 0 ? $"{minutes / 1440} d"
        : minutes >= 60 && minutes % 60 == 0 ? $"{minutes / 60} h"
        : $"{minutes} min";
}

/// <summary>SQL connection settings (Localhost / IP), SLA policy table and admin password.</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly ITAdministrator? _admin;

    private ConnectionMode _mode;
    private LocalInstanceKind _localKind;
    private SqlAuthenticationMode _authentication;
    private string _localInstanceName, _localDbInstance, _ipAddress, _portText, _remoteInstanceName, _database, _userName;
    private bool _encrypt, _trustServerCertificate;
    private int _timeout;
    private string? _testMessage;
    private bool? _testSucceeded;

    public SettingsViewModel(AppServices services, ITAdministrator? admin)
    {
        _services = services;
        _admin = admin;
        var s = services.Configuration.Current;
        _mode = s.Mode;
        _localKind = s.LocalKind;
        _localInstanceName = s.LocalInstanceName;
        _localDbInstance = s.LocalDbInstance;
        _ipAddress = s.IpAddress;
        _portText = s.Port?.ToString() ?? "";
        _remoteInstanceName = s.RemoteInstanceName ?? "";
        _database = s.Database;
        _authentication = s.Authentication;
        _userName = s.UserName ?? "";
        SqlPassword = s.Password ?? "";
        _encrypt = s.Encrypt;
        _trustServerCertificate = s.TrustServerCertificate;
        _timeout = s.ConnectTimeoutSeconds;

        TestCommand = new AsyncRelayCommand(TestAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        LoadSlaCommand = new AsyncRelayCommand(LoadSlaAsync, () => IsAdmin);
        SaveSlaCommand = new AsyncRelayCommand(SaveSlaAsync, () => IsAdmin && SlaRows.Count > 0);
        ChangePasswordCommand = new AsyncRelayCommand(ChangePasswordAsync, () => IsAdmin);
    }

    public override string Title => "Settings";
    public bool IsAdmin => _admin is not null;
    public string SettingsFile => _services.Configuration.SettingsFilePath;

    public ICommand TestCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand LoadSlaCommand { get; }
    public ICommand SaveSlaCommand { get; }
    public ICommand ChangePasswordCommand { get; }

    /// <summary>Raised after a successful save (the shell refreshes its connection banner).</summary>
    public event EventHandler? ConnectionApplied;

    // ---------------- Connection ----------------
    public ConnectionMode Mode { get => _mode; set { if (SetProperty(ref _mode, value)) Changed(nameof(IsLocalhost), nameof(IsIp)); } }
    public bool IsLocalhost => Mode == ConnectionMode.Localhost;
    public bool IsIp => Mode == ConnectionMode.IpAddress;

    public IReadOnlyList<LocalInstanceKind> LocalKinds { get; } = Enum.GetValues<LocalInstanceKind>();
    public LocalInstanceKind LocalKind { get => _localKind; set { if (SetProperty(ref _localKind, value)) Changed(nameof(IsNamedInstance), nameof(IsLocalDb)); } }
    public bool IsNamedInstance => LocalKind == LocalInstanceKind.NamedInstance;
    public bool IsLocalDb => LocalKind == LocalInstanceKind.LocalDb;

    public string LocalInstanceName { get => _localInstanceName; set { if (SetProperty(ref _localInstanceName, value)) Changed(); } }
    public string LocalDbInstance { get => _localDbInstance; set { if (SetProperty(ref _localDbInstance, value)) Changed(); } }
    public string IpAddress { get => _ipAddress; set { if (SetProperty(ref _ipAddress, value)) Changed(); } }
    public string PortText { get => _portText; set { if (SetProperty(ref _portText, value)) Changed(); } }
    public string RemoteInstanceName { get => _remoteInstanceName; set { if (SetProperty(ref _remoteInstanceName, value)) Changed(); } }
    public string Database { get => _database; set { if (SetProperty(ref _database, value)) Changed(); } }

    public SqlAuthenticationMode Authentication { get => _authentication; set { if (SetProperty(ref _authentication, value)) Changed(nameof(IsSqlAuth)); } }
    public bool IsSqlAuth => Authentication == SqlAuthenticationMode.SqlServer;
    public string UserName { get => _userName; set { if (SetProperty(ref _userName, value)) Changed(); } }

    /// <summary>Set from PasswordBox code-behind.</summary>
    public string SqlPassword { get; set; }

    public bool Encrypt { get => _encrypt; set { if (SetProperty(ref _encrypt, value)) Changed(); } }
    public bool TrustServerCertificate { get => _trustServerCertificate; set { if (SetProperty(ref _trustServerCertificate, value)) Changed(); } }
    public int TimeoutSeconds { get => _timeout; set { if (SetProperty(ref _timeout, value)) Changed(); } }

    public string DataSourcePreview
    {
        get
        {
            var s = BuildSettings(out var portError);
            if (portError is not null) return portError;
            var errors = s.Validate();
            return errors.Count > 0 ? errors[0] : $"Data Source = {s.DescribeTarget()}";
        }
    }

    public string? TestMessage { get => _testMessage; private set => SetProperty(ref _testMessage, value); }
    public bool? TestSucceeded { get => _testSucceeded; private set => SetProperty(ref _testSucceeded, value); }

    // ---------------- SLA ----------------
    public ObservableCollection<SlaRowViewModel> SlaRows { get; } = new();

    // ---------------- Password (set from PasswordBoxes) ----------------
    public string CurrentPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
    public string ConfirmPassword { get; set; } = "";
    public event EventHandler? PasswordFieldsResetRequested;

    public async Task InitializeAsync()
    {
        if (IsAdmin) await LoadSlaAsync();
    }

    private DatabaseConnectionSettings BuildSettings(out string? portError)
    {
        portError = null;
        int? port = null;
        if (!string.IsNullOrWhiteSpace(PortText))
        {
            if (int.TryParse(PortText.Trim(), out var p)) port = p;
            else portError = "Port must be a number (e.g. 1433) or empty.";
        }

        return new DatabaseConnectionSettings
        {
            Mode = Mode,
            LocalKind = LocalKind,
            LocalInstanceName = LocalInstanceName,
            LocalDbInstance = LocalDbInstance,
            IpAddress = IpAddress,
            Port = port,
            RemoteInstanceName = string.IsNullOrWhiteSpace(RemoteInstanceName) ? null : RemoteInstanceName,
            Database = Database,
            Authentication = Authentication,
            UserName = UserName,
            Password = SqlPassword,
            Encrypt = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeoutSeconds = TimeoutSeconds
        };
    }

    private async Task TestAsync()
    {
        var settings = BuildSettings(out var portError);
        if (portError is not null) { TestSucceeded = false; TestMessage = portError; return; }
        TestMessage = "Testing…";
        TestSucceeded = null;
        IsBusy = true;
        try
        {
            var result = await ConnectionTester.TestAsync(settings);
            TestSucceeded = result.Success && result.SchemaReady;
            TestMessage = result.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task SaveAsync()
    {
        var settings = BuildSettings(out var portError);
        if (portError is not null) { ErrorMessage = portError; return; }

        var test = await ConnectionTester.TestAsync(settings);
        TestSucceeded = test.Success && test.SchemaReady;
        TestMessage = test.Message;
        if (!test.Success && !_services.Dialogs.Confirm($"The connection test failed:\n\n{test.Message}\n\nSave these settings anyway?"))
            return;

        await RunAsync(() =>
        {
            _services.Configuration.Apply(settings, persist: true);
            return Task.CompletedTask;
        }, "Connection settings saved and applied.");
        ConnectionApplied?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadSlaAsync()
    {
        await RunAsync(async () =>
        {
            var policies = await _services.SlaPolicies.GetAllAsync();
            SlaRows.Clear();
            foreach (var p in policies) SlaRows.Add(new SlaRowViewModel(p));
        });
    }

    private async Task SaveSlaAsync()
    {
        var targets = SlaRows.Select(r => (r.Priority, r.ResponseMinutes, r.ResolutionMinutes)).ToList();
        await RunAsync(() => _services.SlaPolicies.SaveAsync(targets),
            "SLA policies saved. They apply to tickets created or re-prioritised from now on.");
    }

    private async Task ChangePasswordAsync()
    {
        if (_admin is null) return;
        if (NewPassword != ConfirmPassword) { ErrorMessage = "The new password and its confirmation do not match."; return; }
        var policy = Pbkdf2PasswordHasher.CheckPolicy(NewPassword);
        if (policy.Count > 0) { ErrorMessage = string.Join(Environment.NewLine, policy); return; }

        await RunAsync(() => _services.Auth.ChangePasswordAsync(_admin, CurrentPassword, NewPassword), "Password changed.");
        CurrentPassword = NewPassword = ConfirmPassword = "";
        PasswordFieldsResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Changed(params string[] extra)
    {
        foreach (var name in extra) OnPropertyChanged(name);
        OnPropertyChanged(nameof(DataSourcePreview));
    }

    /// <summary>Called by the view when the SQL PasswordBox changes.</summary>
    public void OnSqlPasswordChanged(string password)
    {
        SqlPassword = password;
        TestSucceeded = null;
    }
}
