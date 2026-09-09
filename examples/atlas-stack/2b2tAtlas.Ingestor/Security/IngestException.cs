namespace Atlas.Ingestor.Security;

/// <summary>Base exception for expected ingestion failures that can be reported without an implementation stack trace.</summary>
/// <param name="message">Human-readable failure description.</param>
/// <param name="innerException">Optional underlying exception.</param>
public class IngestException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Indicates that untrusted input violated a security invariant or configured resource ceiling.</summary>
/// <param name="message">Human-readable failure description.</param>
/// <param name="innerException">Optional underlying exception.</param>
/// <remarks>Callers must fail closed; retrying unchanged input is not expected to succeed.</remarks>
public sealed class InputSecurityException(string message, Exception? innerException = null)
    : IngestException(message, innerException);

/// <summary>Indicates malformed, unsupported, ambiguous, or contextually invalid ingestion input.</summary>
/// <param name="message">Human-readable failure description.</param>
/// <param name="innerException">Optional underlying exception.</param>
public sealed class InputValidationException(string message, Exception? innerException = null)
    : IngestException(message, innerException);
