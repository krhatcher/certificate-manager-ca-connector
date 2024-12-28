using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Shared;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
public interface IConfigurationService
{
    Task<ConnectorConfig> GetConfigAsync();
    Task SaveConfigAsync(ConnectorConfig config);
    Task<bool> IsConfiguredAsync();
}

[SupportedOSPlatform("windows")]
public class ConfigurationService : IConfigurationService
{
    private readonly ILogger<ConfigurationService> _logger;
    private readonly ISecureStorage _secureStorage;
    private readonly string _configPath;

    public ConfigurationService(
        ILogger<ConfigurationService> logger,
        ISecureStorage secureStorage)
    {
        _logger = logger;
        _secureStorage = secureStorage;
        _configPath = Constants.ConfigPath;

        // Ensure directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
    }

    public async Task<ConnectorConfig> GetConfigAsync()
    {
        try
        {
            var config = new ConnectorConfig();

            if (File.Exists(_configPath))
            {
                var json = await File.ReadAllTextAsync(_configPath);
                config = JsonSerializer.Deserialize<ConnectorConfig>(json) ?? config;
            }

            // Get API key from secure storage
            config.ApiKey = await _secureStorage.GetApiKeyAsync() ?? string.Empty;

            return config;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load configuration");
            throw;
        }
    }

    public async Task SaveConfigAsync(ConnectorConfig config)
    {
        try
        {
            _logger.LogInformation("Saving configuration...");

            // Save API key to secure storage
            if (!string.IsNullOrEmpty(config.ApiKey))
            {
                await _secureStorage.SaveApiKeyAsync(config.ApiKey);
                _logger.LogInformation("API key saved to secure storage");
            }

            // Create a sanitized config without sensitive data
            var fileConfig = new ConnectorConfig
            {
                Url = config.Url,
                ConnectorId = config.ConnectorId,
                IsRegistered = config.IsRegistered,
                SyncIntervalSeconds = config.SyncIntervalSeconds
                // ApiKey is intentionally omitted
            };

            var json = JsonSerializer.Serialize(fileConfig, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(_configPath, json);
            _logger.LogInformation("Configuration saved to {path}", _configPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save configuration");
            throw;
        }
    }

    public async Task<bool> IsConfiguredAsync()
    {
        try
        {
            var config = await GetConfigAsync();
            return !string.IsNullOrEmpty(config.Url) && 
                   !string.IsNullOrEmpty(config.ApiKey);
        }
        catch
        {
            return false;
        }
    }
} 