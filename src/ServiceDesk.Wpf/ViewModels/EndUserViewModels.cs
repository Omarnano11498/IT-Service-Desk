using System.Collections.ObjectModel;
using System.Windows.Input;
using ServiceDesk.Application.Models;
using ServiceDesk.Domain;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Rules;
using ServiceDesk.Wpf.Mvvm;
using ServiceDesk.Wpf.Services;

namespace ServiceDesk.Wpf.ViewModels;

/// <summary>End-user intake form (no password).</summary>
public sealed class SubmitTicketViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private TicketCategory? _selectedCategory;
    private string _fullName = "", _email = "", _phone = "", _department = "", _subject = "", _description = "";
    private string _assetTag = "", _deviceType = "", _appName = "", _appVersion = "", _networkLocation = "", _ipAddress = "";
    private Urgency _urgency = Urgency.Medium;
    private Impact _impact = Impact.Medium;
    private string? _lastTicketNumber;

    public SubmitTicketViewModel(AppServices services)
    {
        _services = services;
        LoadCategoriesCommand = new AsyncRelayCommand(LoadCategoriesAsync);
        SubmitCommand = new AsyncRelayCommand(SubmitAsync, () => !IsBusy && SelectedCategory is not null);
        ClearCommand = new RelayCommand(ClearForm);
    }

    public override string Title => "Submit a ticket";

    public ObservableCollection<TicketCategory> Categories { get; } = new();
    public IReadOnlyList<Urgency> Urgencies { get; } = Enum.GetValues<Urgency>();
    public IReadOnlyList<Impact> Impacts { get; } = Enum.GetValues<Impact>();

    public ICommand LoadCategoriesCommand { get; }
    public ICommand SubmitCommand { get; }
    public ICommand ClearCommand { get; }

    public TicketCategory? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!SetProperty(ref _selectedCategory, value)) return;
            OnPropertyChanged(nameof(ShowHardware));
            OnPropertyChanged(nameof(ShowSoftware));
            OnPropertyChanged(nameof(ShowNetwork));
        }
    }

    public bool ShowHardware => SelectedCategory?.CategoryType == TicketCategoryType.Hardware;
    public bool ShowSoftware => SelectedCategory?.CategoryType == TicketCategoryType.Software;
    public bool ShowNetwork => SelectedCategory?.CategoryType == TicketCategoryType.Network;

    public string FullName { get => _fullName; set => SetProperty(ref _fullName, value); }
    public string Email { get => _email; set => SetProperty(ref _email, value); }
    public string Phone { get => _phone; set => SetProperty(ref _phone, value); }
    public string Department { get => _department; set => SetProperty(ref _department, value); }
    public string Subject { get => _subject; set => SetProperty(ref _subject, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public string AssetTag { get => _assetTag; set => SetProperty(ref _assetTag, value); }
    public string DeviceType { get => _deviceType; set => SetProperty(ref _deviceType, value); }
    public string ApplicationName { get => _appName; set => SetProperty(ref _appName, value); }
    public string ApplicationVersion { get => _appVersion; set => SetProperty(ref _appVersion, value); }
    public string NetworkLocation { get => _networkLocation; set => SetProperty(ref _networkLocation, value); }
    public string AffectedIpAddress { get => _ipAddress; set => SetProperty(ref _ipAddress, value); }

    public Urgency Urgency
    {
        get => _urgency;
        set { if (SetProperty(ref _urgency, value)) OnPropertyChanged(nameof(PreviewPriority)); }
    }

    public Impact Impact
    {
        get => _impact;
        set { if (SetProperty(ref _impact, value)) OnPropertyChanged(nameof(PreviewPriority)); }
    }

    /// <summary>Live ITIL matrix preview so the requester understands the resulting priority.</summary>
    public TicketPriority PreviewPriority => PriorityMatrix.Calculate(Urgency, Impact);

    public string? LastTicketNumber { get => _lastTicketNumber; private set => SetProperty(ref _lastTicketNumber, value); }

    private async Task LoadCategoriesAsync()
    {
        await RunAsync(async () =>
        {
            var list = await _services.Tickets.GetCategoriesAsync();
            Categories.Clear();
            foreach (var c in list) Categories.Add(c);
            SelectedCategory ??= Categories.FirstOrDefault();
        });
    }

    private async Task SubmitAsync()
    {
        SubmitTicketResult? result = null;
        var ok = await RunAsync(async () =>
        {
            result = await _services.Tickets.SubmitAsync(new SubmitTicketRequest
            {
                FullName = FullName, Email = Email, Phone = Phone, Department = Department,
                CategoryId = SelectedCategory?.Id ?? 0, Urgency = Urgency, Impact = Impact,
                Subject = Subject, Description = Description,
                AssetTag = AssetTag, DeviceType = DeviceType,
                ApplicationName = ApplicationName, ApplicationVersion = ApplicationVersion,
                NetworkLocation = NetworkLocation, AffectedIpAddress = AffectedIpAddress
            });
        });

        if (ok && result is not null)
        {
            LastTicketNumber = result.TicketNumber;
            StatusMessage = $"Ticket {result.TicketNumber} submitted with priority {result.Priority}. " +
                            $"Target first response by {result.ResponseDueAtUtc.ToLocalTime():g}, resolution by {result.ResolutionDueAtUtc.ToLocalTime():g}. " +
                            "Keep the ticket number and your e-mail to track it.";
            Subject = Description = AssetTag = DeviceType = ApplicationName = ApplicationVersion = NetworkLocation = AffectedIpAddress = "";
        }
    }

    private void ClearForm()
    {
        FullName = Email = Phone = Department = Subject = Description = "";
        AssetTag = DeviceType = ApplicationName = ApplicationVersion = NetworkLocation = AffectedIpAddress = "";
        Urgency = Urgency.Medium;
        Impact = Impact.Medium;
        ErrorMessage = null;
        StatusMessage = null;
        LastTicketNumber = null;
    }
}

/// <summary>Requester status tracker + optional satisfaction rating.</summary>
public sealed class TrackTicketViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private string _ticketNumber = "", _email = "", _ratingComment = "";
    private TicketDetail? _detail;
    private int _rating = 5;

    public TrackTicketViewModel(AppServices services)
    {
        _services = services;
        LookupCommand = new AsyncRelayCommand(LookupAsync, () => !string.IsNullOrWhiteSpace(TicketNumber) && !string.IsNullOrWhiteSpace(Email));
        RateCommand = new AsyncRelayCommand(RateAsync, () => CanRate);
    }

    public override string Title => "Track my ticket";

    public ICommand LookupCommand { get; }
    public ICommand RateCommand { get; }

    public string TicketNumber { get => _ticketNumber; set => SetProperty(ref _ticketNumber, value); }
    public string Email { get => _email; set => SetProperty(ref _email, value); }

    public TicketDetail? Detail
    {
        get => _detail;
        private set
        {
            if (!SetProperty(ref _detail, value)) return;
            OnPropertyChanged(nameof(Ticket));
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(CanRate));
            OnPropertyChanged(nameof(Evaluation));
            OnPropertyChanged(nameof(CurrentSlaState));
        }
    }

    public Ticket? Ticket => Detail?.Ticket;
    public ServiceEvaluation? Evaluation => Detail?.Evaluation;
    public bool HasResult => Detail is not null;
    public SlaState? CurrentSlaState => Ticket is null ? null : SlaMonitor.Evaluate(Ticket, _services.Clock.UtcNow);
    public ObservableCollection<TicketHistoryEntry> History { get; } = new();

    public bool CanRate => Ticket?.Status is TicketStatus.Resolved or TicketStatus.Closed;
    public IReadOnlyList<int> RatingOptions { get; } = new[] { 5, 4, 3, 2, 1 };
    public int Rating { get => _rating; set => SetProperty(ref _rating, value); }
    public string RatingComment { get => _ratingComment; set => SetProperty(ref _ratingComment, value); }

    private async Task LookupAsync()
    {
        TicketDetail? found = null;
        await RunAsync(async () => found = await _services.Tickets.TrackAsync(TicketNumber, Email));
        Detail = found;
        History.Clear();
        if (found is null)
        {
            if (ErrorMessage is null) ErrorMessage = "No ticket matches this number and e-mail address.";
            return;
        }
        foreach (var h in found.History.Reverse()) History.Add(h);   // newest first
        StatusMessage = $"Last refreshed {DateTime.Now:t}.";
    }

    private async Task RateAsync()
    {
        if (Ticket is null) return;
        var ok = await RunAsync(() => _services.Tickets.RateAsync(Ticket.TicketNumber, Email, (byte)Rating, RatingComment));
        if (ok)
        {
            await LookupAsync();
            StatusMessage = "Thank you! Your rating has been recorded.";
        }
    }
}
