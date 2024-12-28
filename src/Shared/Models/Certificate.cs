using System;

namespace Shared.Models
{
    public class Certificate
    {
        public string CommonName { get; set; } = string.Empty;
        public DateTime NotAfter { get; set; }
        public string TemplateName { get; set; } = string.Empty;
        public int RequestId { get; set; }
        public string RequesterName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime? RevokedWhen { get; set; }
    }
} 