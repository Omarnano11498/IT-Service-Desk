using System.Diagnostics;
using Microsoft.Data.SqlClient;
using ServiceDesk.Data.Configuration;

namespace ServiceDesk.Data.Connection;

public interface IDbConnectionFactory
{
    Task<SqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default);
}

/// <summary>Opens pooled connections using the Singleton configuration (picks up changes immediately).</summary>
public sealed class SqlConnectionFactory : IDbConnectionFactory
{
    private readonly AppConfigurationManager _config;

    public SqlConnectionFactory(AppConfigurationManager config) => _config = config;

    public async Task<SqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
    {
        var connection = new SqlConnection(_config.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

public sealed record ConnectionTestResult(bool Success, bool SchemaReady, string Message, string? ServerVersion, string? ServerName, TimeSpan Elapsed);

/// <summary>"Test connection" button: validates settings, opens a connection, checks the schema.</summary>
public static class ConnectionTester
{
    public static async Task<ConnectionTestResult> TestAsync(DatabaseConnectionSettings settings, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var errors = settings.Validate();
        if (errors.Count > 0)
            return new ConnectionTestResult(false, false, string.Join(Environment.NewLine, errors), null, null, sw.Elapsed);

        try
        {
            await using var connection = new SqlConnection(ConnectionStringFactory.Build(settings));
            await connection.OpenAsync(ct);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128)),
                       CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(256)),
                       CASE WHEN OBJECT_ID(N'dbo.Tickets') IS NOT NULL
                             AND OBJECT_ID(N'dbo.TicketHistory') IS NOT NULL
                             AND OBJECT_ID(N'dbo.ServiceEvaluations') IS NOT NULL THEN 1 ELSE 0 END;
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var version = reader.GetString(0);
            var server = reader.GetString(1);
            var schemaReady = reader.GetInt32(2) == 1;
            sw.Stop();

            var message = schemaReady
                ? $"Connected to {server} (SQL Server {version}) in {sw.ElapsedMilliseconds} ms. Schema is ready."
                : $"Connected to {server}, but the ServiceDesk tables were not found. Run database\\ServiceDesk_Setup.sql first.";
            return new ConnectionTestResult(true, schemaReady, message, version, server, sw.Elapsed);
        }
        catch (SqlException ex)
        {
            sw.Stop();
            return new ConnectionTestResult(false, false, Explain(ex), null, null, sw.Elapsed);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            sw.Stop();
            return new ConnectionTestResult(false, false, ex.Message, null, null, sw.Elapsed);
        }
    }

    /// <summary>Maps common SQL error numbers to actionable hints.</summary>
    public static string Explain(SqlException ex) => ex.Number switch
    {
        -1 or 2 or 53 => "Server not reachable. Check the IP/instance, that SQL Server is running, TCP/IP is enabled in SQL Server Configuration Manager, and the firewall allows the port (default 1433).",
        -2 => "Connection timed out. The server is reachable but slow or blocked by a firewall.",
        18456 => "Login failed. Check the user name/password, or that SQL Server authentication (mixed mode) is enabled.",
        4060 => "Cannot open the database. Create it with database\\ServiceDesk_Setup.sql or check the database name.",
        26 => "Instance not found. Check the instance name and that the SQL Server Browser service is running (or use an explicit port).",
        _ when ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
            => "TLS certificate not trusted. Tick 'Trust server certificate' for local/dev servers or install a trusted certificate.",
        _ => $"SQL error {ex.Number}: {ex.Message}"
    };
}
