using SocketIOClient;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using Shared.Models;
using Shared.Services;

namespace Shared.Services
{
    public class SocketService : IDisposable
    {
        private SocketIOClient.SocketIO? _socket;
        private readonly ILogger<SocketService> _logger;
        private readonly HttpClient _httpClient;
        private bool _isConnected;
        private Timer? _configRefreshTimer;
        private string _currentUrl = string.Empty;
        private string _currentApiKey = string.Empty;

        public string CurrentUrl => _currentUrl;
        public bool IsConnected => _isConnected;

        public event Action<CertificateAuthorityConfig[]>? CertificateAuthoritiesConfigured;
        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;

        public SocketService(
            ILogger<SocketService> logger, 
            HttpClient httpClient)
        {
            _logger = logger;
            _httpClient = httpClient;
        }

        public void ConfigurePollingService(ICertificatePollingService pollingService)
        {
            pollingService.ProgressChanged += OnCertificateProgressChanged;
        }

        private void OnCertificateProgressChanged(object? sender, CertificateProgress progress)
        {
            try
            {
                if (_socket == null || !_isConnected)
                {
                    _logger.LogDebug("Socket not ready, skipping progress update: {message}", progress.Message);
                    return;
                }

                // Queue the emit on the thread pool to avoid blocking
                Task.Run(async () =>
                {
                    try
                    {
                        await _socket.EmitAsync(SocketMessage.CONNECTOR_TO_API, new
                        {
                            type = "status-update",
                            data = "sync in progress"
                        });
                        _logger.LogDebug("Progress update sent: {message}", progress.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error sending progress update: {message}", progress.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in progress handler");
            }
        }

        public async Task ConnectAsync(string serverUrl, string apiKey)
        {
            if (_isConnected)
            {
                _logger.LogInformation("Already connected to {url}, disconnecting first", _currentUrl);
                await DisconnectAsync();
            }

            try
            {
                // Validate URL
                if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
                {
                    throw new ArgumentException($"Invalid URL format: {serverUrl}");
                }

                _currentUrl = uri.ToString();
                _currentApiKey = apiKey;

                // Register with the API first
                await RegisterWithApi();

                // Start config refresh timer only if not already running
                if (_configRefreshTimer == null)
                {
                    _configRefreshTimer = new Timer(async _ => await RefreshConfiguration(), 
                        null, 
                        TimeSpan.FromMinutes(5),  // Initial delay
                        TimeSpan.FromMinutes(5)); // Subsequent intervals
                }

                // Setup Socket.IO connection
                _socket = new SocketIOClient.SocketIO(serverUrl, new SocketIOOptions
                {
                    Reconnection = true,
                    Path = "/ws",
                    ReconnectionAttempts = 3,
                    ReconnectionDelay = 2000,
                    Transport = SocketIOClient.Transport.TransportProtocol.WebSocket,
                    ConnectionTimeout = TimeSpan.FromSeconds(30),
                    ExtraHeaders = new Dictionary<string, string>
                    {
                        { "x-api-key", apiKey ?? "" }
                    },
                    Query = new Dictionary<string, string>
                    {
                        { "api-key", apiKey ?? "" }
                    }
                });

                // Setup event handlers before connecting
                _socket.OnConnected += (sender, args) =>
                {
                    _isConnected = true;
                    _logger.LogInformation("Socket.IO connected successfully to {url}", _currentUrl);
                    OnConnected?.Invoke(this, EventArgs.Empty);
                };

                _socket.OnDisconnected += (sender, args) =>
                {
                    _isConnected = false;
                    _logger.LogWarning("Socket.IO disconnected from {url}", _currentUrl);
                    OnDisconnected?.Invoke(this, EventArgs.Empty);
                    
                    // Try to reconnect if this wasn't an intentional disconnect
                    if (_socket != null)
                    {
                        _logger.LogInformation("Attempting to reconnect...");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(2000); // Wait before reconnecting
                                await _socket.ConnectAsync();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to reconnect");
                            }
                        });
                    }
                };

                _socket.OnError += (sender, error) =>
                {
                    _isConnected = false;
                    _logger.LogError("Socket.IO error for {url}: {error}", _currentUrl, error);
                    OnError?.Invoke(this, error);
                };

                _logger.LogInformation("Starting Socket.IO connection to {url}...", _currentUrl);
                await _socket.ConnectAsync();
                _logger.LogInformation("Socket.IO connection attempt to {url} completed", _currentUrl);
            }
            catch (Exception ex)
            {
                _isConnected = false;
                _logger.LogError(ex, "Failed to connect Socket.IO to {url}", _currentUrl);
                throw;
            }
        }

        private async Task RegisterWithApi()
        {
            try
            {
                _logger.LogInformation("Registering with API at {url}", _currentUrl);

                var registrationUrl = new Uri(new Uri(_currentUrl), "/api/ca-connector/register").ToString();
                
                using var request = new HttpRequestMessage(HttpMethod.Post, registrationUrl);
                request.Headers.Add("x-api-key", _currentApiKey);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new { serverName = Environment.MachineName }), 
                    System.Text.Encoding.UTF8, 
                    "application/json");

                _logger.LogInformation("Sending registration request with payload: {payload}", 
                    await request.Content.ReadAsStringAsync());

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                _logger.LogInformation("Received registration response: Length {response}", content.Length);
                
                response.EnsureSuccessStatusCode();

                var registration = JsonSerializer.Deserialize<RegistrationResponse>(content, 
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (registration?.Success == true)
                {
                    _logger.LogInformation("Registration successful. Managing {count} CAs", 
                        registration.CertificateAuthorities.Length);
                    
                    // Raise the event instead of directly configuring
                    CertificateAuthoritiesConfigured?.Invoke(registration.CertificateAuthorities);
                }
                else
                {
                    _logger.LogError("Registration failed with response: {response}", content);
                    throw new Exception($"Registration failed: {registration?.Error ?? "Unknown error"}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register with API. Exception details: {details}", 
                    ex.ToString());
                throw;
            }
        }

        private async Task RefreshConfiguration()
        {
            try
            {
                await RegisterWithApi();
                _logger.LogInformation("Configuration refreshed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh configuration");
            }
        }

        public async Task EmitMessageAsync(string eventName, object data)
        {
            if (_socket == null || !_isConnected)
            {
                throw new InvalidOperationException("Socket is not connected");
            }

            await _socket.EmitAsync(eventName, data);
        }

        public async Task DisconnectAsync()
        {
            _configRefreshTimer?.Dispose();
            _configRefreshTimer = null;

            if (_socket != null)
            {
                try
                {
                    if (_isConnected)
                    {
                        // Notify API of disconnect
                        var disconnectUrl = new Uri(new Uri(_currentUrl), "/api/ca-connector/disconnect").ToString();
                        using var request = new HttpRequestMessage(HttpMethod.Post, disconnectUrl);
                        request.Headers.Add("x-api-key", _currentApiKey);
                        await _httpClient.SendAsync(request);
                    }

                    await _socket.DisconnectAsync();
                    _socket.Dispose();
                    _socket = null;
                    _isConnected = false;
                    _logger.LogInformation("Socket.IO disconnected");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disconnecting Socket.IO");
                }
            }
        }

        public void Dispose()
        {
            _configRefreshTimer?.Dispose();
            DisconnectAsync().Wait();
        }
    }

    public class SocketEventData
    {
        public string Event { get; set; } = string.Empty;
        public object? Data { get; set; }
    }

    public static class SocketMessage
    {
        public const string CONNECTOR_TO_API = "connector:to:api";
    }
} 