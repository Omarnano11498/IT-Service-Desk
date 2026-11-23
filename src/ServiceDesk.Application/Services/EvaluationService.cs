using ServiceDesk.Application.Abstractions;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Strategies;

namespace ServiceDesk.Application.Services;

/// <summary>Computes and persists per-ticket service evaluations using a pluggable <see cref="IEvaluationStrategy"/>.</summary>
public sealed class EvaluationService
{
    private readonly IUnitOfWorkFactory _uowFactory;
    private readonly IEvaluationStrategy _strategy;
    private readonly IClock _clock;

    public EvaluationService(IUnitOfWorkFactory uowFactory, IEvaluationStrategy strategy, IClock clock)
    {
        _uowFactory = uowFactory;
        _strategy = strategy;
        _clock = clock;
    }

    public string FormulaVersion => _strategy.FormulaVersion;

    /// <summary>
    /// Evaluates inside the caller's transaction. When <paramref name="rating"/> is null the previous
    /// requester rating/comment (if any) is preserved.
    /// </summary>
    public async Task<ServiceEvaluation?> EvaluateAsync(IUnitOfWork uow, int ticketId, byte? rating, string? comment, CancellationToken ct = default)
    {
        var input = await uow.Evaluations.GetInputAsync(ticketId, ct);
        if (input is null) return null;   // not resolved → nothing to evaluate

        var existing = await uow.Evaluations.GetByTicketAsync(ticketId, ct);
        var effectiveRating = rating ?? existing?.SatisfactionRating;
        var effectiveComment = rating is null ? existing?.RequesterComment : comment;

        var result = _strategy.Evaluate(input with { SatisfactionRating = effectiveRating });
        var evaluation = ServiceEvaluation.FromResult(ticketId, result, effectiveRating, effectiveComment, _strategy.FormulaVersion, _clock.UtcNow);
        await uow.Evaluations.UpsertAsync(evaluation, ct);
        return evaluation;
    }

    /// <summary>Re-scores every resolved ticket (after seeding, or after SLA/formula changes). O(n) round-trips in one transaction.</summary>
    public async Task<int> RecalculateAllAsync(CancellationToken ct = default)
    {
        await using var uow = await _uowFactory.CreateAsync(transactional: true, ct);
        var ids = await uow.Tickets.GetResolvedTicketIdsAsync(ct);
        var count = 0;
        foreach (var id in ids)
            if (await EvaluateAsync(uow, id, null, null, ct) is not null) count++;
        await uow.CommitAsync(ct);
        return count;
    }
}
