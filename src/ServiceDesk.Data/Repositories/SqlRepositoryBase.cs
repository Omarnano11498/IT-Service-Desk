using System.Data;
using Microsoft.Data.SqlClient;

namespace ServiceDesk.Data.Repositories;

/// <summary>Shared ADO.NET plumbing: every command is parameterised and enlisted in the UoW transaction.</summary>
public abstract class SqlRepositoryBase
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction? _transaction;

    protected SqlRepositoryBase(SqlConnection connection, SqlTransaction? transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    protected SqlCommand Command(string sql)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _transaction;
        cmd.CommandType = CommandType.Text;
        cmd.CommandTimeout = 30;
        return cmd;
    }
}

internal static class SqlExtensions
{
    public static SqlCommand With(this SqlCommand cmd, string name, SqlDbType type, object? value, int size = 0)
    {
        var p = cmd.Parameters.Add(name, type);
        if (size != 0) p.Size = size;
        p.Value = value ?? DBNull.Value;
        return cmd;
    }

    public static string Str(this SqlDataReader r, string col) => r.GetString(r.GetOrdinal(col));

    public static string? NStr(this SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    public static int Int(this SqlDataReader r, string col) => r.GetInt32(r.GetOrdinal(col));

    public static int? NInt(this SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetInt32(i);
    }

    public static short Short(this SqlDataReader r, string col) => r.GetInt16(r.GetOrdinal(col));

    public static byte Byte(this SqlDataReader r, string col) => r.GetByte(r.GetOrdinal(col));

    public static byte? NByte(this SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetByte(i);
    }

    public static bool Bool(this SqlDataReader r, string col) => r.GetBoolean(r.GetOrdinal(col));

    public static decimal Dec(this SqlDataReader r, string col) => r.GetDecimal(r.GetOrdinal(col));

    public static double? NDouble(this SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetDouble(i);
    }

    public static byte[] Bytes(this SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? Array.Empty<byte>() : (byte[])r.GetValue(i);
    }

    /// <summary>All timestamps are stored as UTC in DATETIME2; restore the Kind on read.</summary>
    public static DateTime Utc(this SqlDataReader r, string col) =>
        DateTime.SpecifyKind(r.GetDateTime(r.GetOrdinal(col)), DateTimeKind.Utc);

    public static DateTime? NUtc(this SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);
    }

    /// <summary>Escapes LIKE wildcards so user text is matched literally (used with ESCAPE '\').</summary>
    public static string ToLikeContains(this string text) =>
        "%" + text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[") + "%";
}
