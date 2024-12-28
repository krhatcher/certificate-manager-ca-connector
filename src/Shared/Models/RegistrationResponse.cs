using System.Text.Json.Serialization;

namespace Shared.Models
{
    public class CertificateAuthorityConfig
    {
        [JsonPropertyName("config")]
        public string Config { get; set; } = string.Empty;

        [JsonPropertyName("syncInterval")]
        public int SyncInterval { get; set; }
    }

    public class RegistrationResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("apiKey")]
        public string ApiKey { get; set; } = string.Empty;

        [JsonPropertyName("syncInterval")]
        public int SyncInterval { get; set; }

        [JsonPropertyName("certificateAuthorities")]
        public CertificateAuthorityConfig[] CertificateAuthorities { get; set; } = Array.Empty<CertificateAuthorityConfig>();
    }
} 