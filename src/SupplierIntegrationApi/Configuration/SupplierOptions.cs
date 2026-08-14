namespace SupplierIntegrationApi.Configuration;

public class SupplierOptions
{
    public const string SectionName = "Supplier";

    public Uri BaseUrl { get; set; } = new("https://supplier.example/");
    public string ApiKey { get; set; } = string.Empty;
    public int PageSize { get; set; } = 100;
    public double RequestTimeoutSeconds { get; set; } = 10;
    public string WebhookSecret { get; set; } = string.Empty;
    public int ScheduledSyncIntervalMinutes { get; set; } = 30;
}
