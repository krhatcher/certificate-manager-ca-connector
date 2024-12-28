namespace Shared
{
    public static class Constants
    {
        public static readonly string AppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SCFT", "CA Connector");

        public static readonly string ConfigPath = Path.Combine(AppDataPath, "appsettings.json");
        public static readonly string LogPath = Path.Combine(AppDataPath, "logs");
        public static readonly string SecureStoragePath = Path.Combine(AppDataPath, "secure");
    }
} 