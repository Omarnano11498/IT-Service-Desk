using System.Data;
using Microsoft.Data.SqlClient;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.Models;
using ServiceDesk.Domain;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Factories;

namespace ServiceDesk.Data.Repositories;

public sealed class SqlTicketRepository : SqlRepositoryBase, ITicketRepository
{
    private const string SelectColumns = """
        SELECT t.TicketId, t.TicketNumber, t.RequesterId, r.FullName AS RequesterName, r.Email AS RequesterEmail,
               t.CategoryId, c.CategoryName, c.CategoryType, t.PriorityId, t.StatusId, t.Urgency, t.Impact,
               t.Subject, t.Description, t.AssignedAdminId, a.FullName AS AdminName,
               t.CreatedAt, t.FirstResponseAt, t.ResponseDueAt, t.ResolutionDueAt, t.ResolvedAt, t.ClosedAt,
               t.ResolutionNotes, t.RootCause, t.AssetTag, t.DeviceType, t.ApplicationName, t.ApplicationVersion,
               t.NetworkLocation, t.AffectedIpAddress, t.RowVersion
        FROM dbo.Tickets t
        JOIN dbo.Users r            ON r.UserId = t.RequesterId
        JOIN dbo.TicketCategories c ON c.CategoryId = t.CategoryId
        LEFT JOIN dbo.Users a       ON a.UserId = t.AssignedAdminId
        """;

    private readonly ITicketFactory _factory;

    public SqlTicketRepository(SqlConnection connection, SqlTransaction? transaction, ITicketFactory factory)
        : base(connection, transaction) => _factory = factory;

    public async Task<(int Id, string TicketNumber)> InsertAsync(Ticket ticket, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            INSERT dbo.Tickets (RequesterId, CategoryId, PriorityId, StatusId, Urgency, Impact, Subject, Description,
                                CreatedAt, ResponseDueAt, ResolutionDueAt,
                                AssetTag, DeviceType, ApplicationName, ApplicationVersion, NetworkLocation, AffectedIpAddress)
            OUTPUT INSERTED.TicketId, INSERTED.TicketNumber, INSERTED.RowVersion
            VALUES (@RequesterId, @CategoryId, @PriorityId, @StatusId, @Urgency, @Impact, @Subject, @Description,
                    @CreatedAt, @ResponseDueAt, @ResolutionDueAt,
                    @AssetTag, @DeviceType, @ApplicationName, @ApplicationVersion, @NetworkLocation, @AffectedIpAddress);
            """)
            .With("@RequesterId", SqlDbType.Int, ticket.RequesterId)
            .With("@CategoryId", SqlDbType.SmallInt, ticket.CategoryId)
            .With("@PriorityId", SqlDbType.TinyInt, (byte)ticket.Priority)
            .With("@StatusId", SqlDbType.TinyInt, (byte)ticket.Status)
            .With("@Urgency", SqlDbType.TinyInt, (byte)ticket.Urgency)
            .With("@Impact", SqlDbType.TinyInt, (byte)ticket.Impact)
            .With("@Subject", SqlDbType.NVarChar, ticket.Subject, 200)
            .With("@Description", SqlDbType.NVarChar, ticket.Description, -1)
            .With("@CreatedAt", SqlDbType.DateTime2, ticket.CreatedAt)
            .With("@ResponseDueAt", SqlDbType.DateTime2, ticket.ResponseDueAt)
            .With("@ResolutionDueAt", SqlDbType.DateTime2, ticket.ResolutionDueAt);
        AddSubtypeParameters(cmd, ticket);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new DataException("Ticket insert returned no identity.");
        ticket.Id = reader.Int("TicketId");
        ticket.TicketNumber = reader.Str("TicketNumber");
        ticket.RowVersion = reader.Bytes("RowVersion");
        return (ticket.Id, ticket.TicketNumber);
    }

    public async Task<Ticket?> GetByIdAsync(int ticketId, CancellationToken ct = default)
    {
        await using var cmd = Command(SelectColumns + " WHERE t.TicketId = @Id;")
            .With("@Id", SqlDbType.Int, ticketId);
        return (await ReadAsync(cmd, ct)).FirstOrDefault();
    }

    public async Task<Ticket?> GetByNumberAsync(string ticketNumber, CancellationToken ct = default)
    {
        await using var cmd = Command(SelectColumns + " WHERE t.TicketNumber = @Number;")
            .With("@Number", SqlDbType.VarChar, ticketNumber, 12);
        return (await ReadAsync(cmd, ct)).FirstOrDefault();
    }

    public async Task UpdateAsync(Ticket ticket, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            UPDATE dbo.Tickets
               SET PriorityId = @PriorityId, StatusId = @StatusId, AssignedAdminId = @AdminId,
                   FirstResponseAt = @FirstResponseAt, ResponseDueAt = @ResponseDueAt, ResolutionDueAt = @ResolutionDueAt,
                   ResolvedAt = @ResolvedAt, ClosedAt = @ClosedAt, ResolutionNotes = @ResolutionNotes, RootCause = @RootCause
            OUTPUT INSERTED.RowVersion
             WHERE TicketId = @TicketId AND RowVersion = @RowVersion;
            """)
            .With("@PriorityId", SqlDbType.TinyInt, (byte)ticket.Priority)
            .With("@StatusId", SqlDbType.TinyInt, (byte)ticket.Status)
            .With("@AdminId", SqlDbType.Int, ticket.AssignedAdminId)
            .With("@FirstResponseAt", SqlDbType.DateTime2, ticket.FirstResponseAt)
            .With("@ResponseDueAt", SqlDbType.DateTime2, ticket.ResponseDueAt)
            .With("@ResolutionDueAt", SqlDbType.DateTime2, ticket.ResolutionDueAt)
            .With("@ResolvedAt", SqlDbType.DateTime2, ticket.ResolvedAt)
            .With("@ClosedAt", SqlDbType.DateTime2, ticket.ClosedAt)
            .With("@ResolutionNotes", SqlDbType.NVarChar, ticket.ResolutionNotes, -1)
            .With("@RootCause", SqlDbType.NVarChar, ticket.RootCause, 1000)
            .With("@TicketId", SqlDbType.Int, ticket.Id)
            .With("@RowVersion", SqlDbType.Timestamp, ticket.RowVersion);

        var newVersion = await cmd.ExecuteScalarAsync(ct);
        if (newVersion is not byte[] rv) throw new ConcurrencyException(ticket.TicketNumber);
        ticket.RowVersion = rv;
    }

    public async Task<IReadOnlyList<Ticket>> SearchAsync(TicketFilter f, CancellationToken ct = default)
    {
        // "Catch-all" query; OPTION (RECOMPILE) gives an optimal plan for each filter combination.
        var select = SelectColumns.Replace("SELECT t.TicketId", "SELECT TOP (@MaxRows) t.TicketId");
        await using var cmd = Command(select + """

            WHERE (@OpenOnly = 0 OR t.StatusId NOT IN (6, 7))
              AND (@StatusId   IS NULL OR t.StatusId = @StatusId)
              AND (@PriorityId IS NULL OR t.PriorityId = @PriorityId)
              AND (@CategoryId IS NULL OR t.CategoryId = @CategoryId)
              AND (@AdminId    IS NULL OR t.AssignedAdminId = @AdminId)
              AND (@Search IS NULL
                   OR t.TicketNumber LIKE @Search ESCAPE '\'
                   OR t.Subject      LIKE @Search ESCAPE '\'
                   OR r.FullName     LIKE @Search ESCAPE '\'
                   OR r.Email        LIKE @Search ESCAPE '\')
            ORDER BY t.PriorityId, t.ResolutionDueAt, t.TicketId
            OPTION (RECOMPILE);
            """)
            .With("@MaxRows", SqlDbType.Int, Math.Clamp(f.MaxRows, 1, 10_000))
            .With("@OpenOnly", SqlDbType.Bit, f.OpenOnly)
            .With("@StatusId", SqlDbType.TinyInt, (byte?)f.Status)
            .With("@PriorityId", SqlDbType.TinyInt, (byte?)f.Priority)
            .With("@CategoryId", SqlDbType.SmallInt, f.CategoryId)
            .With("@AdminId", SqlDbType.Int, f.AssignedAdminId)
            .With("@Search", SqlDbType.NVarChar, string.IsNullOrWhiteSpace(f.SearchText) ? null : f.SearchText.Trim().ToLikeContains(), 260);
        return await ReadAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<Ticket>> GetCreatedBetweenAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        await using var cmd = Command(SelectColumns + " WHERE t.CreatedAt >= @From AND t.CreatedAt < @To;")
            .With("@From", SqlDbType.DateTime2, fromUtc)
            .With("@To", SqlDbType.DateTime2, toUtc);
        return await ReadAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<int>> GetResolvedTicketIdsAsync(CancellationToken ct = default)
    {
        await using var cmd = Command("SELECT TicketId FROM dbo.Tickets WHERE ResolvedAt IS NOT NULL ORDER BY TicketId;");
        var ids = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt32(0));
        return ids;
    }

    // ------------------------------------------------------------------

    private async Task<IReadOnlyList<Ticket>> ReadAsync(SqlCommand cmd, CancellationToken ct)
    {
        var list = new List<Ticket>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    /// <summary>Factory decides the subclass from CategoryType; then the common + subtype state is restored.</summary>
    private Ticket Map(SqlDataReader r)
    {
        var t = _factory.CreateEmpty((TicketCategoryType)r.Byte("CategoryType"));
        t.Id = r.Int("TicketId");
        t.TicketNumber = r.Str("TicketNumber");
        t.RequesterId = r.Int("RequesterId");
        t.RequesterName = r.Str("RequesterName");
        t.RequesterEmail = r.Str("RequesterEmail");
        t.CategoryId = r.Short("CategoryId");
        t.CategoryName = r.Str("CategoryName");
        t.Priority = (TicketPriority)r.Byte("PriorityId");
        t.Status = (TicketStatus)r.Byte("StatusId");
        t.Urgency = (Urgency)r.Byte("Urgency");
        t.Impact = (Impact)r.Byte("Impact");
        t.Subject = r.Str("Subject");
        t.Description = r.Str("Description");
        t.AssignedAdminId = r.NInt("AssignedAdminId");
        t.AssignedAdminName = r.NStr("AdminName");
        t.CreatedAt = r.Utc("CreatedAt");
        t.FirstResponseAt = r.NUtc("FirstResponseAt");
        t.ResponseDueAt = r.Utc("ResponseDueAt");
        t.ResolutionDueAt = r.Utc("ResolutionDueAt");
        t.ResolvedAt = r.NUtc("ResolvedAt");
        t.ClosedAt = r.NUtc("ClosedAt");
        t.ResolutionNotes = r.NStr("ResolutionNotes");
        t.RootCause = r.NStr("RootCause");
        t.RowVersion = r.Bytes("RowVersion");

        switch (t)
        {
            case HardwareTicket hw:
                hw.AssetTag = r.NStr("AssetTag");
                hw.DeviceType = r.NStr("DeviceType");
                break;
            case SoftwareTicket sw:
                sw.ApplicationName = r.NStr("ApplicationName");
                sw.ApplicationVersion = r.NStr("ApplicationVersion");
                break;
            case NetworkTicket nw:
                nw.NetworkLocation = r.NStr("NetworkLocation");
                nw.AffectedIpAddress = r.NStr("AffectedIpAddress");
                break;
        }
        return t;
    }

    private static void AddSubtypeParameters(SqlCommand cmd, Ticket ticket)
    {
        var hw = ticket as HardwareTicket;
        var sw = ticket as SoftwareTicket;
        var nw = ticket as NetworkTicket;
        cmd.With("@AssetTag", SqlDbType.NVarChar, hw?.AssetTag, 50)
           .With("@DeviceType", SqlDbType.NVarChar, hw?.DeviceType, 60)
           .With("@ApplicationName", SqlDbType.NVarChar, sw?.ApplicationName, 100)
           .With("@ApplicationVersion", SqlDbType.NVarChar, sw?.ApplicationVersion, 40)
           .With("@NetworkLocation", SqlDbType.NVarChar, nw?.NetworkLocation, 100)
           .With("@AffectedIpAddress", SqlDbType.VarChar, nw?.AffectedIpAddress, 45);
    }
}
