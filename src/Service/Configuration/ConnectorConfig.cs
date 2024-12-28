public class ConnectorConfig
{
    public string Url { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ConnectorId { get; set; } = string.Empty;
    public bool IsRegistered { get; set; }
    public int SyncIntervalSeconds { get; set; } = 300;
} 