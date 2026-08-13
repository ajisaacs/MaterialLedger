namespace MaterialLedger.Data.Idempotency;

public record IdempotentResult<T>(T Result, bool WasReplayed);
