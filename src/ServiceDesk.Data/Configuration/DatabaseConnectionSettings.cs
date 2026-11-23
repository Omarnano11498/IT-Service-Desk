using System.Net;
using System.Text.Json.Serialization;

namespace ServiceDesk.Data.Configuration;

public enum ConnectionMode { Localhost, IpAddress }

public enum LocalInstanceKind { DefaultInstance, NamedInstance, LocalDb }

public enum SqlAuthenticationMode { Windows, SqlServer }

/// <summary>User-editable connection parameters (persisted to %AppData%\ServiceDesk\connection.json).</summary>
public sealed class DatabaseConnectionSettings
{
    public ConnectionMode Mode { get; set; } = ConnectionMode.Localhost;

    // ---- Localhost path ----
    public LocalInstanceKind LocalKind { get; set; } = LocalInstanceKind.NamedInstance;
    public string LocalInstanceName { get; set; } = "SQLEXPRESS";
    public string LocalDbInstance { get; set; } = "MSSQLLocalDB";

    // ---- Remote IP path ----
    public string IpAddress { get; set; } = "192.168.1.50";
    public int? Port { get; set; } = 1433;
    public string? RemoteInstanceName { get; set; }

    // ---- Common ----
    public string Database { get; set; } = "ServiceDesk";
    public SqlAuthenticationMode Authentication { get; set; } = SqlAuthenticationMode.Windows;
    public string? UserName { get; set; }

    /// <summary>Plain password in memory only; persisted encrypted with DPAPI (see <see cref="AppConfigurationManager"/>).</summary>
    [JsonIgnore] public string? Password { get; set; }

    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; } = true;
    public int ConnectTimeoutSeconds { get; set; } = 15;

    public DatabaseConnectionSettings Clone() => (DatabaseConnectionSettings)MemberwiseClone();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Database)) errors.Add("Database name is required.");
        else if (Database.Length > 128 || Database.IndexOfAny(new[] { ';', '[', ']', '\'' }) >= 0) errors.Add("Database name contains invalid characters.");

        if (Mode == ConnectionMode.Localhost)
        {
            if (LocalKind == LocalInstanceKind.NamedInstance && string.IsNullOrWhiteSpace(LocalInstanceName))
                errors.Add("Instance name is required for a named local instance (e.g. SQLEXPRESS).");
            if (LocalKind == LocalInstanceKind.LocalDb && string.IsNullOrWhiteSpace(LocalDbInstance))
                errors.Add("LocalDB instance name is required (e.g. MSSQLLocalDB).");
        }
        else
        {
            if (!System.Net.IPAddress.TryParse(IpAddress?.Trim(), out _))
                errors.Add("Enter a valid IPv4 or IPv6 address (e.g. 192.168.1.50).");
            if (Port is < 1 or > 65535) errors.Add("Port must be between 1 and 65535.");
        }

        if (Authentication == SqlAuthenticationMode.SqlServer && string.IsNullOrWhiteSpace(UserName))
            errors.Add("SQL login name is required for SQL Server authentication.");
        if (ConnectTimeoutSeconds is < 1 or > 120) errors.Add("Timeout must be between 1 and 120 seconds.");
        return errors;
    }

    public string DescribeTarget()
    {
        var builder = ConnectionStringFactory.ResolveBuilder(Mode);
        return $"{builder.BuildDataSource(this)} / {Database}";
    }
}
