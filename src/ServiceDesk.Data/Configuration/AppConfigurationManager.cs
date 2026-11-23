using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServiceDesk.Data.Configuration;

/// <summary>
/// Thread-safe Singleton (Lazy&lt;T&gt; with ExecutionAndPublication) holding the active SQL connection configuration.
/// Persists settings as JSON under %AppData%\ServiceDesk; the SQL password is encrypted with Windows DPAPI
/// (CurrentUser scope) so it is never written in plain text.
/// </summary>
public sealed class AppConfigurationManager
{
    private static readonly Lazy<AppConfigurationManager> LazyInstance =
        new(() => new AppConfigurationManager(), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ServiceDesk.Wpf.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly object _sync = new();
    private readonly string _filePath;
    private DatabaseConnectionSettings _settings = new();
    private string? _connectionString;

    public static AppConfigurationManager Instance => LazyInstance.Value;

    /// <summary>Observer hook: raised after settings are applied.</summary>
    public event EventHandler? SettingsChanged;

    private AppConfigurationManager()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServiceDesk");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "connection.json");
        HasSavedSettings = TryLoad();
    }

    public bool HasSavedSettings { get; private set; }

    public string SettingsFilePath => _filePath;

    /// <summary>Defensive copy so callers cannot mutate shared state.</summary>
    public DatabaseConnectionSettings Current
    {
        get { lock (_sync) return _settings.Clone(); }
    }

    public string ConnectionString
    {
        get
        {
            lock (_sync)
                return _connectionString ??= ConnectionStringFactory.Build(_settings);
        }
    }

    public void Apply(DatabaseConnectionSettings settings, bool persist)
    {
        var connectionString = ConnectionStringFactory.Build(settings);   // validates first
        lock (_sync)
        {
            _settings = settings.Clone();
            _connectionString = connectionString;
            if (persist)
            {
                Save(_settings);
                HasSavedSettings = true;
            }
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryLoad()
    {
        try
        {
            if (!File.Exists(_filePath)) return false;
            var dto = JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(_filePath), JsonOptions);
            if (dto?.Settings is null) return false;
            dto.Settings.Password = Unprotect(dto.ProtectedPassword);
            _settings = dto.Settings;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or UnauthorizedAccessException or FormatException)
        {
            return false;   // corrupt or foreign file → fall back to defaults; user re-enters settings
        }
    }

    private void Save(DatabaseConnectionSettings settings)
    {
        var dto = new PersistedSettings { Settings = settings, ProtectedPassword = Protect(settings.Password) };
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dto, JsonOptions));
        File.Move(tmp, _filePath, overwrite: true);   // atomic replace
    }

    private static string? Protect(string? plain) =>
        string.IsNullOrEmpty(plain) ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string? cipher) =>
        string.IsNullOrEmpty(cipher) ? null
            : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser));

    private sealed class PersistedSettings
    {
        public DatabaseConnectionSettings? Settings { get; set; }
        public string? ProtectedPassword { get; set; }
    }
}
