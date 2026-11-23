namespace ServiceDesk.Domain;

/// <summary>Raised when a business rule is violated (invalid transition, missing data...).</summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

/// <summary>Raised when input validation fails; carries every error so the UI can show them together.</summary>
public sealed class ValidationException : DomainException
{
    public IReadOnlyList<string> Errors { get; }

    public ValidationException(IEnumerable<string> errors)
        : this(errors.ToList()) { }

    private ValidationException(List<string> errors)
        : base(string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }
}

/// <summary>Raised when another administrator modified the same ticket first (rowversion mismatch).</summary>
public sealed class ConcurrencyException : DomainException
{
    public ConcurrencyException(string ticketNumber)
        : base($"Ticket {ticketNumber} was modified by another administrator. Refresh the queue and try again.") { }
}
