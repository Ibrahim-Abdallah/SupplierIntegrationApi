namespace SupplierIntegrationApi.Services;

public class SupplierException(string code, string safeMessage, Exception? inner = null) : Exception(safeMessage, inner)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
}

public sealed class SyncAlreadyRunningException() : Exception("A supplier synchronization is already running.");
