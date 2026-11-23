using ServiceDesk.Application.Models;
using ServiceDesk.Domain;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Strategies;

namespace ServiceDesk.Application.Abstractions;

public interface IClock { DateTime UtcNow { get; } }

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

// ------------------------- Repository pattern -------------------------

public interface ITicketRepository
{
    Task<(int Id, string TicketNumber)> InsertAsync(Ticket ticket, CancellationToken ct = default);
    Task<Ticket?> GetByIdAsync(int ticketId, CancellationToken ct = default);
    Task<Ticket?> GetByNumberAsync(string ticketNumber, CancellationToken ct = default);

    /// <summary>Optimistic update: throws <see cref="ConcurrencyException"/> when the rowversion changed.</summary>
    Task UpdateAsync(Ticket ticket, CancellationToken ct = default);

    Task<IReadOnlyList<Ticket>> SearchAsync(TicketFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<Ticket>> GetCreatedBetweenAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    Task<IReadOnlyList<int>> GetResolvedTicketIdsAsync(CancellationToken ct = default);
}

public interface IUserRepository
{
    /// <summary>Insert or refresh the requester row identified by e-mail; returns UserId.</summary>
    Task<int> UpsertEndUserAsync(EndUser user, CancellationToken ct = default);
    Task<EndUser?> GetEndUserByEmailAsync(string email, CancellationToken ct = default);
    Task<ITAdministrator?> GetAdminByUserNameAsync(string userName, CancellationToken ct = default);
    Task<ITAdministrator?> GetAdminByIdAsync(int adminId, CancellationToken ct = default);
    Task UpdateLastLoginAsync(int adminId, DateTime utc, CancellationToken ct = default);
    Task UpdatePasswordAsync(int adminId, byte[] hash, byte[] salt, int iterations, CancellationToken ct = default);
}

public interface ISlaPolicyRepository
{
    Task<IReadOnlyList<SlaPolicy>> GetAllAsync(CancellationToken ct = default);
    Task<SlaPolicy> GetAsync(TicketPriority priority, CancellationToken ct = default);
    Task UpdateAsync(SlaPolicy policy, CancellationToken ct = default);
}

public interface ICategoryRepository
{
    Task<IReadOnlyList<TicketCategory>> GetActiveAsync(CancellationToken ct = default);
    Task<TicketCategory?> GetAsync(short categoryId, CancellationToken ct = default);
}

public interface ITicketHistoryRepository
{
    Task AddAsync(int ticketId, TicketHistoryEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<TicketHistoryEntry>> GetByTicketAsync(int ticketId, bool includeInternal, CancellationToken ct = default);
}

public interface IEvaluationRepository
{
    /// <summary>Builds evaluation facts (reopen/escalation/pending counts come from TicketHistory).</summary>
    Task<EvaluationInput?> GetInputAsync(int ticketId, CancellationToken ct = default);
    Task<ServiceEvaluation?> GetByTicketAsync(int ticketId, CancellationToken ct = default);
    Task UpsertAsync(ServiceEvaluation evaluation, CancellationToken ct = default);
    Task DeleteByTicketAsync(int ticketId, CancellationToken ct = default);
    Task<IReadOnlyList<ServiceEvaluation>> GetForTicketsCreatedBetweenAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}

// ------------------------- Unit of Work -------------------------

/// <summary>One connection + (optional) one transaction shared by all repositories.</summary>
public interface IUnitOfWork : IAsyncDisposable
{
    ITicketRepository Tickets { get; }
    IUserRepository Users { get; }
    ISlaPolicyRepository SlaPolicies { get; }
    ICategoryRepository Categories { get; }
    ITicketHistoryRepository History { get; }
    IEvaluationRepository Evaluations { get; }

    Task CommitAsync(CancellationToken ct = default);
}

public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> CreateAsync(bool transactional, CancellationToken ct = default);
}

// ------------------------- Security -------------------------

public sealed record PasswordHashResult(byte[] Hash, byte[] Salt, int Iterations);

public interface IPasswordHasher
{
    PasswordHashResult Hash(string password);
    bool Verify(string password, byte[] hash, byte[] salt, int iterations);
}
