using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Shared;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
public interface ISecureStorage
{
    Task SaveApiKeyAsync(string apiKey);
    Task<string?> GetApiKeyAsync();
}

[SupportedOSPlatform("windows")]
public class SecureStorage : ISecureStorage
{
    private readonly string _keyPath;
    private readonly ILogger<SecureStorage> _logger;

    public SecureStorage(ILogger<SecureStorage> logger)
    {
        _logger = logger;
        _keyPath = Path.Combine(Constants.SecureStoragePath, "apikey.dat");
        
        // Ensure directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
    }

    public async Task SaveApiKeyAsync(string apiKey)
    {
        try
        {
            var encryptedData = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(apiKey),
                null,
                DataProtectionScope.LocalMachine
            );

            await File.WriteAllBytesAsync(_keyPath, encryptedData);
            _logger.LogInformation("API key saved to secure storage");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save API key to secure storage");
            throw;
        }
    }

    public async Task<string?> GetApiKeyAsync()
    {
        try
        {
            if (!File.Exists(_keyPath))
            {
                _logger.LogInformation("No API key file found at: {path}", _keyPath);
                return null;
            }

            var encryptedData = await File.ReadAllBytesAsync(_keyPath);
            var decryptedData = ProtectedData.Unprotect(
                encryptedData,
                null,
                DataProtectionScope.LocalMachine
            );

            return Encoding.UTF8.GetString(decryptedData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read API key from secure storage");
            return null;
        }
    }
} 