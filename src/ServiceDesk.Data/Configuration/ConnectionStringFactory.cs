using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;

namespace ServiceDesk.Data.Configuration;

/// <summary>Builds the "Data Source" part for one connection path.</summary>
public interface IDataSourceBuilder
{
    ConnectionMode Mode { get; }
    string BuildDataSource(DatabaseConnectionSettings settings);
}

/// <summary>localhost | .\SQLEXPRESS | (localdb)\MSSQLLocalDB</summary>
public sealed class LocalhostDataSourceBuilder : IDataSourceBuilder
{
    public ConnectionMode Mode => ConnectionMode.Localhost;

    public string BuildDataSource(DatabaseConnectionSettings s) => s.LocalKind switch
    {
        LocalInstanceKind.DefaultInstance => "localhost",
        LocalInstanceKind.NamedInstance => $@".\{s.LocalInstanceName.Trim()}",
        LocalInstanceKind.LocalDb => $@"(localdb)\{s.LocalDbInstance.Trim()}",
        _ => "localhost"
    };
}

/// <summary>
/// tcp:192.168.1.50,1433 (explicit port) | 192.168.1.50\INSTANCE (named instance via SQL Browser) | tcp:192.168.1.50,1433 (default).
/// An explicit port bypasses SQL Browser (UDP 1434) and is the most reliable option across firewalls.
/// </summary>
public sealed class RemoteIpDataSourceBuilder : IDataSourceBuilder
{
    public ConnectionMode Mode => ConnectionMode.IpAddress;

    public string BuildDataSource(DatabaseConnectionSettings s)
    {
        var host = (s.IpAddress ?? string.Empty).Trim();
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            host = ip.ToString();   // normalise e.g. "192.168.001.050"

        var prefix = "tcp:" + host;
        if (s.Port is { } port) return $"{prefix},{port}";
        if (!string.IsNullOrWhiteSpace(s.RemoteInstanceName)) return $@"{host}\{s.RemoteInstanceName.Trim()}";
        return $"{prefix},1433";
    }
}

/// <summary>
/// Factory: picks the data-source builder by <see cref="ConnectionMode"/> and assembles a full,
/// injection-safe connection string through <see cref="SqlConnectionStringBuilder"/> (never string concatenation).
/// </summary>
public static class ConnectionStringFactory
{
    private static readonly IReadOnlyDictionary<ConnectionMode, IDataSourceBuilder> Builders =
        new Dictionary<ConnectionMode, IDataSourceBuilder>
        {
            [ConnectionMode.Localhost] = new LocalhostDataSourceBuilder(),
            [ConnectionMode.IpAddress] = new RemoteIpDataSourceBuilder()
        };

    public static IDataSourceBuilder ResolveBuilder(ConnectionMode mode) =>
        Builders.TryGetValue(mode, out var b) ? b : throw new NotSupportedException($"Connection mode {mode} is not supported.");

    public static string Build(DatabaseConnectionSettings settings, string? databaseOverride = null)
    {
        var errors = settings.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        var csb = new SqlConnectionStringBuilder
        {
            DataSource = ResolveBuilder(settings.Mode).BuildDataSource(settings),
            InitialCatalog = databaseOverride ?? settings.Database.Trim(),
            ConnectTimeout = settings.ConnectTimeoutSeconds,
            Encrypt = settings.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = settings.TrustServerCertificate,
            ApplicationName = "IT Service Desk (WPF)",
            MultipleActiveResultSets = false,
            Pooling = true
        };

        if (settings.Authentication == SqlAuthenticationMode.Windows)
        {
            csb.IntegratedSecurity = true;
        }
        else
        {
            csb.IntegratedSecurity = false;
            csb.UserID = settings.UserName!.Trim();
            csb.Password = settings.Password ?? string.Empty;
        }

        return csb.ConnectionString;
    }
}
