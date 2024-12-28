namespace Shared.Models
{
    public class CertificateProgress
    {
        public string Message { get; set; } = string.Empty;
        public int ProcessedCount { get; set; }
        public int TotalCount { get; set; }
        public string CaName { get; set; } = string.Empty;
        public bool IsComplete { get; set; }
        public string? Error { get; set; }
    }
} 