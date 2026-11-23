using System.Data;
using Microsoft.Data.SqlClient;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Data.Connection;
using ServiceDesk.Data.Repositories;
using ServiceDesk.Domain.Factories;

namespace ServiceDesk.Data;

/// <summary>
/// Unit of Work over one SqlConnection + optional SqlTransaction. Repositories are created lazily and
/// all share the same connection/transaction, so a use case commits or rolls back atomically.
/// Disposing without <see cref="CommitAsync"/> rolls back.
/// </summary>
public sealed class SqlUnitOfWork : IUnitOfWork
{
    private readonly SqlConnection _connection;
    private readonly SqlTransaction? _transaction;
    private readonly ITicketFactory _ticketFactory;
    private bool _completed;

    private ITicketRepository? _tickets;
    private IUserRepository? _users;
    private ISlaPolicyRepository? _sla;
    private ICategoryRepository? _categories;
    private ITicketHistoryRepository? _history;
    private IEvaluationRepository? _evaluations;

    private SqlUnitOfWork(SqlConnection connection, SqlTransaction? transaction, ITicketFactory ticketFactory)
    {
        _connection = connection;
        _transaction = transaction;
        _ticketFactory = ticketFactory;
    }

    internal static async Task<SqlUnitOfWork> CreateAsync(IDbConnectionFactory factory, ITicketFactory ticketFactory, bool transactional, CancellationToken ct)
    {
        var connection = await factory.CreateOpenConnectionAsync(ct);
        try
        {
            SqlTransaction? tx = transactional
                ? (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
                : null;
            return new SqlUnitOfWork(connection, tx, ticketFactory);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public ITicketRepository Tickets => _tickets ??= new SqlTicketRepository(_connection, _transaction, _ticketFactory);
    public IUserRepository Users => _users ??= new SqlUserRepository(_connection, _transaction);
    public ISlaPolicyRepository SlaPolicies => _sla ??= new SqlSlaPolicyRepository(_connection, _transaction);
    public ICategoryRepository Categories => _categories ??= new SqlCategoryRepository(_connection, _transaction);
    public ITicketHistoryRepository History => _history ??= new SqlTicketHistoryRepository(_connection, _transaction);
    public IEvaluationRepository Evaluations => _evaluations ??= new SqlEvaluationRepository(_connection, _transaction);

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_completed) throw new InvalidOperationException("Unit of work already completed.");
        if (_transaction is not null) await _transaction.CommitAsync(ct);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            if (!_completed)
            {
                try { await _transaction.RollbackAsync(); }
                catch (InvalidOperationException) { /* already rolled back by the server (e.g. deadlock victim) */ }
            }
            await _transaction.DisposeAsync();
        }
        await _connection.DisposeAsync();
    }
}

public sealed class SqlUnitOfWorkFactory : IUnitOfWorkFactory
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ITicketFactory _ticketFactory;

    public SqlUnitOfWorkFactory(IDbConnectionFactory connectionFactory, ITicketFactory ticketFactory)
    {
        _connectionFactory = connectionFactory;
        _ticketFactory = ticketFactory;
    }

    public async Task<IUnitOfWork> CreateAsync(bool transactional, CancellationToken ct = default) =>
        await SqlUnitOfWork.CreateAsync(_connectionFactory, _ticketFactory, transactional, ct);
}
