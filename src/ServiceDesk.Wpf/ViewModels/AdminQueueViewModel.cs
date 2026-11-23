using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ServiceDesk.Application.Models;
using ServiceDesk.Application.Services;
using ServiceDesk.Domain;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Rules;
using ServiceDesk.Domain.Strategies;
using ServiceDesk.Wpf.Mvvm;
using ServiceDesk.Wpf.Services;

namespace ServiceDesk.Wpf.ViewModels;

/// <summary>Password-protected entry point for IT administrators.</summary>
public sealed class AdminLoginViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly Action<ITAdministrator> _onLoggedIn;
    private string _userName = "";

    public AdminLoginViewModel(AppServices services, Action<ITAdministrator> onLoggedIn)
    {
        _services = services;
        _onLoggedIn = onLoggedIn;
        LoginCommand = new AsyncRelayCommand(LoginAsync, () => !string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrEmpty(Password));
    }

    public override string Title => "IT administrator sign-in";
    public ICommand LoginCommand { get; }
    public string UserName { get => _userName; set => SetProperty(ref _userName, value); }

    /// <summary>Set from PasswordBox code-behind (PasswordBox.Password is intentionally not bindable).</summary>
    public string Password { get; set; } = "";

    /// <summary>Raised so the view can clear the PasswordBox after an attempt.</summary>
    public event EventHandler? PasswordResetRequested;

    private async Task LoginAsync()
    {
        ITAdministrator? admin = null;
        await RunAsync(async () => admin = await _services.Auth.LoginAsync(UserName, Password));
        Password = "";
        PasswordResetRequested?.Invoke(this, EventArgs.Empty);
        if (admin is not null) _onLoggedIn(admin);
    }
}

/// <summary>One ranked queue row (read-only projection for the DataGrid).</summary>
public sealed class QueueRowViewModel
{
    public QueueRowViewModel(QueueItem item) => Item = item;

    public QueueItem Item { get; }
    public Ticket Ticket => Item.Ticket;
    public int Rank => Item.Rank;
    public int TicketId => Ticket.Id;
    public string TicketNumber => Ticket.TicketNumber;
    public string Subject => Ticket.Subject;
    public string Category => Ticket.CategoryName;
    public string Requester => Ticket.RequesterName;
    public TicketPriority Priority => Ticket.Priority;
    public TicketStatus Status => Ticket.Status;
    public SlaState SlaState => Item.SlaState;
    public bool ResponseOverdue => Item.ResponseOverdue;
    public double Score => Item.UrgencyScore;
    public string AssignedTo => Ticket.AssignedAdminName ?? "—";
    public DateTime DueUtc => Ticket.ResolutionDueAt;
    public string AgeText => FormatSpan(Item.Age);
    public string RemainingText => Ticket.IsOpen
        ? (Item.RemainingResolution >= TimeSpan.Zero ? FormatSpan(Item.RemainingResolution) : "-" + FormatSpan(Item.RemainingResolution.Negate()))
        : "—";
    public bool NeedsAttention => SlaState is SlaState.AtRisk or SlaState.Breached || ResponseOverdue;

    internal static string FormatSpan(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : $"{Math.Max(0, (int)t.TotalMinutes)}m";
}

/// <summary>Admin queue &amp; triage workspace: ordered queue, filters, detail, lifecycle actions, audit trail.</summary>
public sealed class AdminQueueViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _services;
    private readonly ITAdministrator _admin;
    private readonly DispatcherTimer _timer;

    private ITicketOrderingStrategy _selectedStrategy;
    private Option<TicketStatus?> _statusFilter;
    private Option<TicketPriority?> _priorityFilter;
    private Option<short?> _categoryFilter;
    private string _searchText = "";
    private bool _includeClosed;
    private bool _onlyMine;
    private QueueRowViewModel? _selectedRow;
    private TicketDetail? _detail;
    private string _actionNote = "", _resolutionNotes = "", _rootCause = "", _snapshotInfo = "";
    private TicketPriority _newPriority = TicketPriority.Medium;
    private bool _noteIsInternal = true;
    private int _breachedCount, _atRiskCount;

    public AdminQueueViewModel(AppServices services, ITAdministrator admin)
    {
        _services = services;
        _admin = admin;

        Strategies = services.Queue.Strategies;
        _selectedStrategy = Strategies[0];

        StatusOptions = new[] { new Option<TicketStatus?>("All statuses", null) }
            .Concat(Enum.GetValues<TicketStatus>().Select(s => new Option<TicketStatus?>(s.ToDisplay(), s))).ToList();
        PriorityOptions = new[] { new Option<TicketPriority?>("All priorities", null) }
            .Concat(Enum.GetValues<TicketPriority>().Select(p => new Option<TicketPriority?>(p.ToString(), p))).ToList();
        CategoryOptions.Add(new Option<short?>("All categories", null));
        _statusFilter = StatusOptions[0];
        _priorityFilter = PriorityOptions[0];
        _categoryFilter = CategoryOptions[0];

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ReceiveNextCommand = new AsyncRelayCommand(ReceiveNextAsync);
        ClearFiltersCommand = new AsyncRelayCommand(ClearFiltersAsync);

        ClaimCommand = TicketAction(t => t.IsOpen && t.AssignedAdminId != _admin.Id,
            (t, rv) => _services.Tickets.AssignAsync(t.Id, rv, _admin, NoteOrNull), "Ticket received.");
        StartCommand = TicketAction(t => Can(t, TicketStatus.InProgress) && t.Status != TicketStatus.Resolved,
            (t, rv) => _services.Tickets.StartWorkAsync(t.Id, rv, _admin, NoteOrNull), "Work started.");
        PendingCommand = TicketAction(t => Can(t, TicketStatus.Pending),
            (t, rv) => _services.Tickets.PutOnHoldAsync(t.Id, rv, _admin, ActionNote), "Ticket moved to Pending.");
        EscalateCommand = TicketAction(t => Can(t, TicketStatus.Escalated),
            (t, rv) => _services.Tickets.EscalateAsync(t.Id, rv, _admin, ActionNote), "Ticket escalated.");
        ReturnCommand = TicketAction(t => Can(t, TicketStatus.Queued),
            (t, rv) => _services.Tickets.ReturnToQueueAsync(t.Id, rv, _admin, NoteOrNull), "Ticket returned to the queue.");
        ResolveCommand = TicketAction(t => Can(t, TicketStatus.Resolved),
            (t, rv) => _services.Tickets.ResolveAsync(t.Id, rv, _admin, ResolutionNotes, RootCause), "Ticket resolved and evaluated.");
        CloseCommand = TicketAction(t => Can(t, TicketStatus.Closed),
            (t, rv) => _services.Tickets.CloseAsync(t.Id, rv, _admin, NoteOrNull), "Ticket closed.");
        ReopenCommand = TicketAction(t => t.Status == TicketStatus.Resolved,
            (t, rv) => _services.Tickets.ReopenAsync(t.Id, rv, _admin, ActionNote), "Ticket reopened.");
        ChangePriorityCommand = TicketAction(t => t.IsOpen && t.Priority != NewPriority,
            (t, rv) => _services.Tickets.ChangePriorityAsync(t.Id, rv, _admin, NewPriority, NoteOrNull), "Priority updated; SLA deadlines recalculated.");
        AddNoteCommand = TicketAction(t => !string.IsNullOrWhiteSpace(ActionNote),
            (t, _) => _services.Tickets.AddNoteAsync(t.Id, _admin, ActionNote, NoteIsInternal), "Note added.");

        // Observer: queue aging alerts refresh every 30 s; service events refresh on every committed change.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) => { if (!IsBusy) await LoadQueueAsync(keepSelection: true); };
        _timer.Start();
        _services.Tickets.TicketChanged += OnTicketChanged;
    }

    public override string Title => "Queue & triage";
    public string AdminName => _admin.FullName;

    // ---------------- Queue ----------------
    public IReadOnlyList<ITicketOrderingStrategy> Strategies { get; }
    public IReadOnlyList<Option<TicketStatus?>> StatusOptions { get; }
    public IReadOnlyList<Option<TicketPriority?>> PriorityOptions { get; }
    public ObservableCollection<Option<short?>> CategoryOptions { get; } = new();
    public ObservableCollection<QueueRowViewModel> Items { get; } = new();
    public IReadOnlyList<TicketPriority> Priorities { get; } = Enum.GetValues<TicketPriority>();

    public ITicketOrderingStrategy SelectedStrategy
    {
        get => _selectedStrategy;
        set { if (SetProperty(ref _selectedStrategy, value)) _ = LoadQueueAsync(true); }
    }

    public Option<TicketStatus?> StatusFilter { get => _statusFilter; set { if (SetProperty(ref _statusFilter, value)) _ = LoadQueueAsync(true); } }
    public Option<TicketPriority?> PriorityFilter { get => _priorityFilter; set { if (SetProperty(ref _priorityFilter, value)) _ = LoadQueueAsync(true); } }
    public Option<short?> CategoryFilter { get => _categoryFilter; set { if (SetProperty(ref _categoryFilter, value)) _ = LoadQueueAsync(true); } }
    public bool IncludeClosed { get => _includeClosed; set { if (SetProperty(ref _includeClosed, value)) _ = LoadQueueAsync(true); } }
    public bool OnlyMine { get => _onlyMine; set { if (SetProperty(ref _onlyMine, value)) _ = LoadQueueAsync(true); } }
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }

    public string SnapshotInfo { get => _snapshotInfo; private set => SetProperty(ref _snapshotInfo, value); }
    public int BreachedCount { get => _breachedCount; private set => SetProperty(ref _breachedCount, value); }
    public int AtRiskCount { get => _atRiskCount; private set => SetProperty(ref _atRiskCount, value); }
    private string _liveUpdate = "";
    public string LiveUpdate { get => _liveUpdate; private set => SetProperty(ref _liveUpdate, value); }

    public QueueRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set { if (SetProperty(ref _selectedRow, value)) _ = LoadDetailAsync(value?.TicketId); }
    }

    // ---------------- Detail ----------------
    public TicketDetail? Detail
    {
        get => _detail;
        private set
        {
            if (!SetProperty(ref _detail, value)) return;
            OnPropertyChanged(nameof(SelectedTicket));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(AllowedTransitions));
            OnPropertyChanged(nameof(SelectedSlaState));
            if (value is not null) NewPriority = value.Ticket.Priority;
        }
    }

    public Ticket? SelectedTicket => Detail?.Ticket;
    public bool HasSelection => Detail is not null;
    public SlaState? SelectedSlaState => SelectedTicket is null ? null : SlaMonitor.Evaluate(SelectedTicket, _services.Clock.UtcNow);
    public string AllowedTransitions => SelectedTicket is null ? "" :
        "Allowed next states: " + string.Join(", ", TicketStateMachine.AllowedTargets(SelectedTicket.Status).Select(s => s.ToDisplay()));
    public ObservableCollection<TicketHistoryEntry> History { get; } = new();

    public string ActionNote { get => _actionNote; set => SetProperty(ref _actionNote, value); }
    public string ResolutionNotes { get => _resolutionNotes; set => SetProperty(ref _resolutionNotes, value); }
    public string RootCause { get => _rootCause; set => SetProperty(ref _rootCause, value); }
    public TicketPriority NewPriority { get => _newPriority; set => SetProperty(ref _newPriority, value); }
    public bool NoteIsInternal { get => _noteIsInternal; set => SetProperty(ref _noteIsInternal, value); }
    private string? NoteOrNull => string.IsNullOrWhiteSpace(ActionNote) ? null : ActionNote;

    // ---------------- Commands ----------------
    public ICommand RefreshCommand { get; }
    public ICommand ReceiveNextCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand ClaimCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PendingCommand { get; }
    public ICommand EscalateCommand { get; }
    public ICommand ReturnCommand { get; }
    public ICommand ResolveCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ReopenCommand { get; }
    public ICommand ChangePriorityCommand { get; }
    public ICommand AddNoteCommand { get; }

    public async Task InitializeAsync()
    {
        await RunAsync(async () =>
        {
            var categories = await _services.Tickets.GetCategoriesAsync();
            foreach (var c in categories) CategoryOptions.Add(new Option<short?>(c.Name, c.Id));
        });
        await LoadQueueAsync(keepSelection: false);
    }

    private Task RefreshAsync() => LoadQueueAsync(keepSelection: true);

    private async Task ClearFiltersAsync()
    {
        _statusFilter = StatusOptions[0]; _priorityFilter = PriorityOptions[0]; _categoryFilter = CategoryOptions[0];
        _includeClosed = false; _onlyMine = false; SearchText = "";
        OnPropertyChanged(nameof(StatusFilter)); OnPropertyChanged(nameof(PriorityFilter)); OnPropertyChanged(nameof(CategoryFilter));
        OnPropertyChanged(nameof(IncludeClosed)); OnPropertyChanged(nameof(OnlyMine));
        await LoadQueueAsync(true);
    }

    private async Task LoadQueueAsync(bool keepSelection)
    {
        var selectedId = keepSelection ? SelectedRow?.TicketId : null;
        QueueSnapshot? snapshot = null;
        var filter = new TicketFilter
        {
            Status = StatusFilter.Value,
            Priority = PriorityFilter.Value,
            CategoryId = CategoryFilter.Value,
            AssignedAdminId = OnlyMine ? _admin.Id : null,
            SearchText = SearchText,
            OpenOnly = !IncludeClosed && StatusFilter.Value is not (TicketStatus.Resolved or TicketStatus.Closed)
        };

        await RunAsync(async () => snapshot = await _services.Queue.LoadAsync(filter, SelectedStrategy));
        if (snapshot is null) return;

        Items.Clear();
        foreach (var item in snapshot.Items) Items.Add(new QueueRowViewModel(item));
        BreachedCount = snapshot.Items.Count(i => i.Ticket.IsOpen && i.SlaState == SlaState.Breached);
        AtRiskCount = snapshot.Items.Count(i => i.SlaState == SlaState.AtRisk);
        SnapshotInfo = $"{snapshot.Items.Count} tickets • ordered by {snapshot.StrategyName} • merge sort {snapshot.SortDuration.TotalMilliseconds:0.###} ms • as of {snapshot.AsOfUtc.ToLocalTime():T}";

        if (selectedId is { } id)
        {
            var row = Items.FirstOrDefault(r => r.TicketId == id);
            _selectedRow = row;
            OnPropertyChanged(nameof(SelectedRow));
            await LoadDetailAsync(row?.TicketId);
        }
    }

    private async Task LoadDetailAsync(int? ticketId)
    {
        if (ticketId is null) { Detail = null; History.Clear(); return; }
        TicketDetail? detail = null;
        await RunAsync(async () => detail = await _services.Tickets.GetDetailAsync(ticketId.Value));
        Detail = detail;
        History.Clear();
        if (detail is not null)
            foreach (var h in detail.History.Reverse()) History.Add(h);
    }

    private async Task ReceiveNextAsync()
    {
        Ticket? received = null;
        var ok = await RunAsync(async () => received = await _services.Queue.ReceiveNextAsync(_admin, SelectedStrategy));
        if (!ok) return;
        if (received is null) { StatusMessage = "No unclaimed (Queued) tickets left."; return; }
        StatusMessage = $"Received {received.TicketNumber} (top of the {SelectedStrategy.Name} heap).";
        await LoadQueueAsync(false);
        SelectedRow = Items.FirstOrDefault(r => r.TicketId == received.Id);
    }

    /// <summary>Builds a lifecycle command bound to the selected ticket (with optimistic-concurrency rowversion).</summary>
    private ICommand TicketAction(Func<Ticket, bool> canRun, Func<Ticket, byte[], Task<Ticket>> run, string success) =>
        new AsyncRelayCommand(async () =>
        {
            var ticket = SelectedTicket;
            if (ticket is null) return;
            var ok = await RunAsync(() => run(ticket, ticket.RowVersion), success);
            if (ok) { ActionNote = ""; ResolutionNotes = ""; RootCause = ""; }
            await LoadQueueAsync(keepSelection: true);
            if (SelectedRow is null) await LoadDetailAsync(ticket.Id);   // row may be filtered out (e.g. now closed)
        }, () => !IsBusy && SelectedTicket is { } t && canRun(t));

    private static bool Can(Ticket t, TicketStatus target) => TicketStateMachine.CanTransition(t.Status, target);

    private void OnTicketChanged(object? sender, TicketChangedEventArgs e)
    {
        // Ensure UI-thread execution even if the event is raised from a worker thread.
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            LiveUpdate = $"Live: {e.TicketNumber} {e.Action} → {e.Status.ToDisplay()} at {DateTime.Now:T}");
    }

    public void Dispose()
    {
        _timer.Stop();
        _services.Tickets.TicketChanged -= OnTicketChanged;
    }
}
