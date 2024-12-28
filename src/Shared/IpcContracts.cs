namespace Shared
{
    public static class IpcConstants
    {
        public const string PipeName = "CertificateManager_IPC_v1";
        public const string SaveConfigCommand = "SAVE_CONFIG";
        public const string ShowWindowCommand = "SHOW_WINDOW";
        public const string GetConfigCommand = "GET_CONFIG";
    }

    public class ConfigurationMessage
    {
        public string Url { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
    }

    public class IpcMessage
    {
        public string Command { get; set; } = string.Empty;
        public string[]? Parameters { get; set; }
    }

    public class IpcResponse
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public ConfigurationMessage? Config { get; set; }
        public string Command { get; set; } = string.Empty;
    }
} 