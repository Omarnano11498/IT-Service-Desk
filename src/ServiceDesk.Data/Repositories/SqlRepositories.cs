using System.Data;
using Microsoft.Data.SqlClient;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Domain;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Strategies;

namespace ServiceDesk.Data.Repositories;

// =============================== Users ===============================
public sealed class SqlUserRepository : SqlRepositoryBase, IUserRepository
{
    public SqlUserRepository(SqlConnection c, SqlTransaction? t) : base(c, t) { }

    public async Task<int> UpsertEndUserAsync(EndUser user, CancellationToken ct = default)
    {
        // UPDLOCK + HOLDLOCK serialises concurrent first submissions from the same e-mail.
        await using var cmd = Command("""
            DECLARE @id INT;
            SELECT @id = UserId FROM dbo.Users WITH (UPDLOCK, HOLDLOCK) WHERE RoleId = 1 AND Email = @Email;
            IF @id IS NULL
            BEGIN
                INSERT dbo.Users (RoleId, FullName, Email, Phone, Department) VALUES (1, @FullName, @Email, @Phone, @Department);
                SET @id = CAST(SCOPE_IDENTITY() AS INT);
            END
            ELSE
                UPDATE dbo.Users SET FullName = @FullName,
                                     Phone = COALESCE(@Phone, Phone),
                                     Department = COALESCE(@Department, Department)
                 WHERE UserId = @id;
            SELECT @id;
            """)
            .With("@Email", SqlDbType.NVarChar, user.Contact.Email, 254)
            .With("@FullName", SqlDbType.NVarChar, user.FullName, 120)
            .With("@Phone", SqlDbType.NVarChar, user.Contact.Phone, 30)
            .With("@Department", SqlDbType.NVarChar, user.Department, 80);
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        user.Id = id;
        return id;
    }

    public async Task<EndUser?> GetEndUserByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            SELECT UserId, FullName, Email, Phone, Department, IsActive, CreatedAt
            FROM dbo.Users WHERE RoleId = 1 AND Email = @Email;
            """).With("@Email", SqlDbType.NVarChar, email.Trim().ToLowerInvariant(), 254);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new EndUser
        {
            Id = r.Int("UserId"),
            FullName = r.Str("FullName"),
            Contact = new ContactInfo(r.Str("Email"), r.NStr("Phone")),
            Department = r.NStr("Department"),
            IsActive = r.Bool("IsActive"),
            CreatedAt = r.Utc("CreatedAt")
        };
    }

    public Task<ITAdministrator?> GetAdminByUserNameAsync(string userName, CancellationToken ct = default) =>
        GetAdminAsync("UserName = @Key", c => c.With("@Key", SqlDbType.NVarChar, userName.Trim(), 50), ct);

    public Task<ITAdministrator?> GetAdminByIdAsync(int adminId, CancellationToken ct = default) =>
        GetAdminAsync("UserId = @Key", c => c.With("@Key", SqlDbType.Int, adminId), ct);

    public async Task UpdateLastLoginAsync(int adminId, DateTime utc, CancellationToken ct = default)
    {
        await using var cmd = Command("UPDATE dbo.Users SET LastLoginAt = @At WHERE UserId = @Id AND RoleId = 2;")
            .With("@At", SqlDbType.DateTime2, utc)
            .With("@Id", SqlDbType.Int, adminId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdatePasswordAsync(int adminId, byte[] hash, byte[] salt, int iterations, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            UPDATE dbo.Users SET PasswordHash = @Hash, PasswordSalt = @Salt, HashIterations = @Iter
             WHERE UserId = @Id AND RoleId = 2;
            """)
            .With("@Hash", SqlDbType.VarBinary, hash, 64)
            .With("@Salt", SqlDbType.VarBinary, salt, 32)
            .With("@Iter", SqlDbType.Int, iterations)
            .With("@Id", SqlDbType.Int, adminId);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new DomainException("Administrator not found.");
    }

    private async Task<ITAdministrator?> GetAdminAsync(string where, Action<SqlCommand> bind, CancellationToken ct)
    {
        await using var cmd = Command($"""
            SELECT UserId, UserName, FullName, Email, Phone, Department, PasswordHash, PasswordSalt,
                   HashIterations, IsActive, CreatedAt, LastLoginAt
            FROM dbo.Users WHERE RoleId = 2 AND {where};
            """);
        bind(cmd);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ITAdministrator
        {
            Id = r.Int("UserId"),
            UserName = r.Str("UserName"),
            FullName = r.Str("FullName"),
            Contact = new ContactInfo(r.Str("Email"), r.NStr("Phone")),
            Department = r.NStr("Department"),
            PasswordHash = r.Bytes("PasswordHash"),
            PasswordSalt = r.Bytes("PasswordSalt"),
            HashIterations = r.Int("HashIterations"),
            IsActive = r.Bool("IsActive"),
            CreatedAt = r.Utc("CreatedAt"),
            LastLoginAt = r.NUtc("LastLoginAt")
        };
    }
}

// =============================== SLA policies ===============================
public sealed class SqlSlaPolicyRepository : SqlRepositoryBase, ISlaPolicyRepository
{
    public SqlSlaPolicyRepository(SqlConnection c, SqlTransaction? t) : base(c, t) { }

    public async Task<IReadOnlyList<SlaPolicy>> GetAllAsync(CancellationToken ct = default)
    {
        await using var cmd = Command("""
            SELECT PriorityId, PriorityName, ResponseTargetMinutes, ResolutionTargetMinutes, IsActive
            FROM dbo.SlaPolicies ORDER BY PriorityId;
            """);
        var list = new List<SlaPolicy>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task<SlaPolicy> GetAsync(TicketPriority priority, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            SELECT PriorityId, PriorityName, ResponseTargetMinutes, ResolutionTargetMinutes, IsActive
            FROM dbo.SlaPolicies WHERE PriorityId = @Id;
            """).With("@Id", SqlDbType.TinyInt, (byte)priority);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new DomainException($"No SLA policy is configured for priority {priority}.");
        return Map(r);
    }

    public async Task UpdateAsync(SlaPolicy policy, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            UPDATE dbo.SlaPolicies
               SET ResponseTargetMinutes = @Resp, ResolutionTargetMinutes = @Resol, UpdatedAt = SYSUTCDATETIME()
             WHERE PriorityId = @Id;
            """)
            .With("@Resp", SqlDbType.Int, policy.ResponseTargetMinutes)
            .With("@Resol", SqlDbType.Int, policy.ResolutionTargetMinutes)
            .With("@Id", SqlDbType.TinyInt, (byte)policy.Priority);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static SlaPolicy Map(SqlDataReader r) => new()
    {
        Priority = (TicketPriority)r.Byte("PriorityId"),
        PriorityName = r.Str("PriorityName"),
        ResponseTargetMinutes = r.Int("ResponseTargetMinutes"),
        ResolutionTargetMinutes = r.Int("ResolutionTargetMinutes"),
        IsActive = r.Bool("IsActive")
    };
}

// =============================== Categories ===============================
public sealed class SqlCategoryRepository : SqlRepositoryBase, ICategoryRepository
{
    public SqlCategoryRepository(SqlConnection c, SqlTransaction? t) : base(c, t) { }

    public async Task<IReadOnlyList<TicketCategory>> GetActiveAsync(CancellationToken ct = default)
    {
        await using var cmd = Command("""
            SELECT CategoryId, CategoryName, CategoryType, Description, IsActive
            FROM dbo.TicketCategories WHERE IsActive = 1 ORDER BY CategoryType, CategoryName;
            """);
        var list = new List<TicketCategory>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task<TicketCategory?> GetAsync(short categoryId, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            SELECT CategoryId, CategoryName, CategoryType, Description, IsActive
            FROM dbo.TicketCategories WHERE CategoryId = @Id AND IsActive = 1;
            """).With("@Id", SqlDbType.SmallInt, categoryId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    private static TicketCategory Map(SqlDataReader r) => new()
    {
        Id = r.Short("CategoryId"),
        Name = r.Str("CategoryName"),
        CategoryType = (TicketCategoryType)r.Byte("CategoryType"),
        Description = r.NStr("Description"),
        IsActive = r.Bool("IsActive")
    };
}

// =============================== History (append-only) ===============================
public sealed class SqlTicketHistoryRepository : SqlRepositoryBase, ITicketHistoryRepository
{
    public SqlTicketHistoryRepository(SqlConnection c, SqlTransaction? t) : base(c, t) { }

    public async Task AddAsync(int ticketId, TicketHistoryEntry e, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            INSERT dbo.TicketHistory (TicketId, ActionType, FromStatusId, ToStatusId, FromPriorityId, ToPriorityId,
                                      PerformedByUserId, Notes, IsInternal, OccurredAt)
            VALUES (@TicketId, @Action, @FromStatus, @ToStatus, @FromPrio, @ToPrio, @User, @Notes, @Internal, @At);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """)
            .With("@TicketId", SqlDbType.Int, ticketId)
            .With("@Action", SqlDbType.TinyInt, (byte)e.Action)
            .With("@FromStatus", SqlDbType.TinyInt, (byte?)e.FromStatus)
            .With("@ToStatus", SqlDbType.TinyInt, (byte?)e.ToStatus)
            .With("@FromPrio", SqlDbType.TinyInt, (byte?)e.FromPriority)
            .With("@ToPrio", SqlDbType.TinyInt, (byte?)e.ToPriority)
            .With("@User", SqlDbType.Int, e.PerformedByUserId)
            .With("@Notes", SqlDbType.NVarChar, e.Notes is { Length: > 2000 } n ? n[..2000] : e.Notes, 2000)
            .With("@Internal", SqlDbType.Bit, e.IsInternal)
            .With("@At", SqlDbType.DateTime2, e.OccurredAt);
        e.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        e.TicketId = ticketId;
    }

    public async Task<IReadOnlyList<TicketHistoryEntry>> GetByTicketAsync(int ticketId, bool includeInternal, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            SELECT h.HistoryId, h.TicketId, h.ActionType, h.FromStatusId, h.ToStatusId, h.FromPriorityId, h.ToPriorityId,
                   h.PerformedByUserId, u.FullName AS PerformedByName, h.Notes, h.IsInternal, h.OccurredAt
            FROM dbo.TicketHistory h
            LEFT JOIN dbo.Users u ON u.UserId = h.PerformedByUserId
            WHERE h.TicketId = @TicketId AND (@IncludeInternal = 1 OR h.IsInternal = 0)
            ORDER BY h.OccurredAt, h.HistoryId;
            """)
            .With("@TicketId", SqlDbType.Int, ticketId)
            .With("@IncludeInternal", SqlDbType.Bit, includeInternal);

        var list = new List<TicketHistoryEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new TicketHistoryEntry
            {
                Id = r.GetInt64(r.GetOrdinal("HistoryId")),
                TicketId = r.Int("TicketId"),
                Action = (HistoryAction)r.Byte("ActionType"),
                FromStatus = (TicketStatus?)r.NByte("FromStatusId"),
                ToStatus = (TicketStatus?)r.NByte("ToStatusId"),
                FromPriority = (TicketPriority?)r.NByte("FromPriorityId"),
                ToPriority = (TicketPriority?)r.NByte("ToPriorityId"),
                PerformedByUserId = r.NInt("PerformedByUserId"),
                PerformedByName = r.NStr("PerformedByName"),
                Notes = r.NStr("Notes"),
                IsInternal = r.Bool("IsInternal"),
                OccurredAt = r.Utc("OccurredAt")
            });
        }
        return list;
    }
}

// =============================== Evaluations ===============================
public sealed class SqlEvaluationRepository : SqlRepositoryBase, IEvaluationRepository
{
    private const string SelectEvaluation = """
        SELECT e.EvaluationId, e.TicketId, t.TicketNumber, t.PriorityId, a.FullName AS AdminName,
               e.SpeedScore, e.QualityScore, e.OverallScore, e.Grade, e.SlaBreached, e.SatisfactionRating,
               e.RequesterComment, e.FormulaVersion, e.EvaluatedAt,
               CAST(DATEDIFF(SECOND, t.CreatedAt, t.ResolvedAt) AS FLOAT) / 3600.0 AS ResolutionHours
        FROM dbo.ServiceEvaluations e
        JOIN dbo.Tickets t     ON t.TicketId = e.TicketId
        LEFT JOIN dbo.Users a  ON a.UserId = t.AssignedAdminId
        """;

    public SqlEvaluationRepository(SqlConnection c, SqlTransaction? t) : base(c, t) { }

    public async Task<EvaluationInput?> GetInputAsync(int ticketId, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            SELECT t.TicketId, t.CreatedAt, t.FirstResponseAt, t.ResolvedAt, t.ResponseDueAt, t.ResolutionDueAt,
                   SUM(CASE WHEN h.ActionType = 8 THEN 1 ELSE 0 END) AS Reopens,
                   SUM(CASE WHEN h.ActionType = 9 THEN 1 ELSE 0 END) AS Escalations,
                   SUM(CASE WHEN h.ToStatusId = 4 THEN 1 ELSE 0 END) AS Pendings,
                   e.SatisfactionRating
            FROM dbo.Tickets t
            LEFT JOIN dbo.TicketHistory h      ON h.TicketId = t.TicketId
            LEFT JOIN dbo.ServiceEvaluations e ON e.TicketId = t.TicketId
            WHERE t.TicketId = @TicketId AND t.ResolvedAt IS NOT NULL
            GROUP BY t.TicketId, t.CreatedAt, t.FirstResponseAt, t.ResolvedAt, t.ResponseDueAt, t.ResolutionDueAt, e.SatisfactionRating;
            """).With("@TicketId", SqlDbType.Int, ticketId);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new EvaluationInput(
            TicketId: r.Int("TicketId"),
            CreatedAt: r.Utc("CreatedAt"),
            FirstResponseAt: r.NUtc("FirstResponseAt"),
            ResolvedAt: r.Utc("ResolvedAt"),
            ResponseDueAt: r.Utc("ResponseDueAt"),
            ResolutionDueAt: r.Utc("ResolutionDueAt"),
            ReopenCount: r.Int("Reopens"),
            EscalationCount: r.Int("Escalations"),
            PendingCount: r.Int("Pendings"),
            SatisfactionRating: r.NByte("SatisfactionRating"));
    }

    public async Task<ServiceEvaluation?> GetByTicketAsync(int ticketId, CancellationToken ct = default)
    {
        await using var cmd = Command(SelectEvaluation + " WHERE e.TicketId = @TicketId;")
            .With("@TicketId", SqlDbType.Int, ticketId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task UpsertAsync(ServiceEvaluation ev, CancellationToken ct = default)
    {
        await using var cmd = Command("""
            UPDATE dbo.ServiceEvaluations WITH (UPDLOCK, HOLDLOCK)
               SET SpeedScore = @Speed, QualityScore = @Quality, OverallScore = @Overall, Grade = @Grade,
                   SlaBreached = @Breached, SatisfactionRating = @Rating, RequesterComment = @Comment,
                   FormulaVersion = @Formula, EvaluatedAt = @At
             WHERE TicketId = @TicketId;
            IF @@ROWCOUNT = 0
                INSERT dbo.ServiceEvaluations (TicketId, SpeedScore, QualityScore, OverallScore, Grade, SlaBreached,
                                               SatisfactionRating, RequesterComment, FormulaVersion, EvaluatedAt)
                VALUES (@TicketId, @Speed, @Quality, @Overall, @Grade, @Breached, @Rating, @Comment, @Formula, @At);
            """)
            .With("@TicketId", SqlDbType.Int, ev.TicketId)
            .With("@Speed", SqlDbType.Decimal, ev.SpeedScore)
            .With("@Quality", SqlDbType.Decimal, ev.QualityScore)
            .With("@Overall", SqlDbType.Decimal, ev.OverallScore)
            .With("@Grade", SqlDbType.TinyInt, (byte)ev.Grade)
            .With("@Breached", SqlDbType.Bit, ev.SlaBreached)
            .With("@Rating", SqlDbType.TinyInt, ev.SatisfactionRating)
            .With("@Comment", SqlDbType.NVarChar, ev.RequesterComment is { Length: > 1000 } c ? c[..1000] : ev.RequesterComment, 1000)
            .With("@Formula", SqlDbType.VarChar, ev.FormulaVersion, 10)
            .With("@At", SqlDbType.DateTime2, ev.EvaluatedAt);
        foreach (SqlParameter p in cmd.Parameters)
            if (p.SqlDbType == SqlDbType.Decimal) { p.Precision = 5; p.Scale = 2; }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteByTicketAsync(int ticketId, CancellationToken ct = default)
    {
        await using var cmd = Command("DELETE FROM dbo.ServiceEvaluations WHERE TicketId = @TicketId;")
            .With("@TicketId", SqlDbType.Int, ticketId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ServiceEvaluation>> GetForTicketsCreatedBetweenAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        await using var cmd = Command(SelectEvaluation + """

            WHERE t.CreatedAt >= @From AND t.CreatedAt < @To
            ORDER BY e.EvaluatedAt DESC;
            """)
            .With("@From", SqlDbType.DateTime2, fromUtc)
            .With("@To", SqlDbType.DateTime2, toUtc);
        var list = new List<ServiceEvaluation>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    private static ServiceEvaluation Map(SqlDataReader r) => new()
    {
        Id = r.Int("EvaluationId"),
        TicketId = r.Int("TicketId"),
        TicketNumber = r.Str("TicketNumber"),
        Priority = (TicketPriority)r.Byte("PriorityId"),
        AdminName = r.NStr("AdminName"),
        SpeedScore = r.Dec("SpeedScore"),
        QualityScore = r.Dec("QualityScore"),
        OverallScore = r.Dec("OverallScore"),
        Grade = (EvaluationGrade)r.Byte("Grade"),
        SlaBreached = r.Bool("SlaBreached"),
        SatisfactionRating = r.NByte("SatisfactionRating"),
        RequesterComment = r.NStr("RequesterComment"),
        FormulaVersion = r.Str("FormulaVersion"),
        EvaluatedAt = r.Utc("EvaluatedAt"),
        ResolutionHours = r.NDouble("ResolutionHours") is { } h ? Math.Round(h, 2) : null
    };
}
