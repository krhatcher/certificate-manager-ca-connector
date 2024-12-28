using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.IO.Pipes;
using System.Text.Json;
using Shared;
using Shared.Services;
using System.Runtime.Versioning;
using System.Collections.Concurrent;
using System.Security.Principal;
using System.Security.AccessControl;

[SupportedOSPlatform("windows")]
public class ServiceWorker : BackgroundService
{
    private readonly ILogger<ServiceWorker> _logger;
    private readonly IConfigurationService _configService;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly SocketService _socketService;
    private readonly ICertificatePollingService _pollingService;
    private readonly ConcurrentQueue<bool> _connectionStatusQueue = new();

    public ServiceWorker(
        ILogger<ServiceWorker> logger,
        IConfigurationService configService,
        IHostApplicationLifetime appLifetime,
        SocketService socketService,
        ICertificatePollingService pollingService)
    {
        _logger = logger;
        _configService = configService;
        _appLifetime = appLifetime;
        _socketService = socketService;
        _pollingService = pollingService;

        // Subscribe to CA configuration events
        _socketService.CertificateAuthoritiesConfigured += authorities =>
        {
            _logger.LogInformation("Configuring certificate polling for {count} CAs", authorities.Length);
            _pollingService.ConfigurePolling(authorities);
        };

        // Subscribe to socket connection events
        _socketService.OnConnected += (sender, args) =>
        {
            _logger.LogInformation("Socket connected - queueing notification");
            _connectionStatusQueue.Enqueue(true);
        };

        _socketService.OnDisconnected += (sender, args) =>
        {
            _logger.LogInformation("Socket disconnected - queueing notification");
            _connectionStatusQueue.Enqueue(false);
        };

        _socketService.OnError += (sender, error) =>
        {
            _logger.LogError("Socket error - queueing disconnection notification: {error}", error);
            _connectionStatusQueue.Enqueue(false);
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Service starting...");

            // Start IPC server
            _ = RunIpcServer(stoppingToken);

            // Main service loop
            while (!stoppingToken.IsCancellationRequested)
            {
                if (await _configService.IsConfiguredAsync())
                {
                    var config = await _configService.GetConfigAsync();
                    _logger.LogInformation("Service running with URL: {url}", config.Url);

                    // Queue initial pending state if we have a URL but aren't connected
                    if (!string.IsNullOrEmpty(config.Url) && !_socketService.IsConnected)
                    {
                        _logger.LogInformation("Setting initial pending state");
                        _connectionStatusQueue.Enqueue(false);  // This will show as pending since we have a URL
                    }

                    // Connect to Socket.IO if not already connected to the same URL
                    try
                    {
                        if (!_socketService.IsConnected || _socketService.CurrentUrl != config.Url)
                        {
                            _logger.LogInformation("Attempting Socket.IO connection to server...");
                            await _socketService.ConnectAsync(config.Url, config.ApiKey);
                            _logger.LogInformation("Socket.IO connection established");
                            // Queue connected state notification
                            _connectionStatusQueue.Enqueue(true);
                        }
                        else
                        {
                            _logger.LogDebug("Already connected to {url}, skipping connection", config.Url);
                        }

                        // Wait for a while before checking again
                        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to connect to Socket.IO");
                        // Queue disconnected state which will show as pending since we have a URL
                        _connectionStatusQueue.Enqueue(false);
                        // Wait a shorter time before retry on error
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    }
                }
                else
                {
                    _logger.LogWarning("Service not configured - waiting for configuration");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Service shutting down normally");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in service");
            _appLifetime.StopApplication();
            throw;
        }
    }

    private async Task RunIpcServer(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting IPC server with pipe name: {pipeName}", IpcConstants.PipeName);

        // Create pipe security object
        var pipeSecurity = new PipeSecurity();
        
        // Allow builtin users read and write access to the pipe. 
        var id = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        pipeSecurity.SetAccessRule(new PipeAccessRule(id, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        // Deny network service access to the pipe
        var networkServiceId = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        pipeSecurity.SetAccessRule(new PipeAccessRule(networkServiceId, PipeAccessRights.ReadWrite, AccessControlType.Deny));

        // Create a list to track active writers
        var activeWriters = new List<StreamWriter>();

        // Start a task to process the connection status queue
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_connectionStatusQueue.TryDequeue(out bool isConnected))
                    {
                        // Get current config to determine if we should show pending state
                        var currentConfig = await _configService.GetConfigAsync();
                        var isPending = !string.IsNullOrEmpty(currentConfig.Url) && !isConnected;
                        var connectionState = isPending ? "pending" : (isConnected ? "true" : "false");

                        var statusUpdate = new IpcResponse
                        {
                            Success = true,
                            Config = new ConfigurationMessage { Url = connectionState },
                            Command = "connection_status_changed"
                        };

                        var statusJson = JsonSerializer.Serialize(statusUpdate);
                        _logger.LogInformation("Broadcasting connection status update: {status}", isPending ? "Pending" : (isConnected ? "Connected" : "Disconnected"));

                        // Get a snapshot of active writers and clean up disconnected ones
                        List<StreamWriter> currentWriters;
                        lock (activeWriters)
                        {
                            activeWriters.RemoveAll(w => w.BaseStream == null || !((PipeStream)w.BaseStream).IsConnected);
                            currentWriters = new List<StreamWriter>(activeWriters);
                        }

                        // Send to all active writers
                        foreach (var writer in currentWriters)
                        {
                            try
                            {
                                await writer.WriteLineAsync(statusJson);
                                await writer.FlushAsync();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to send status update to client");
                            }
                        }
                    }
                    await Task.Delay(100, cancellationToken); // Check queue every 100ms
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing connection status queue");
                    await Task.Delay(1000, cancellationToken); // Wait longer on error
                }
            }
        }, cancellationToken);

        // Start a task to handle status listeners
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream? statusPipe = null;
                StreamReader? reader = null;
                StreamWriter? writer = null;

                try
                {
                    // statusPipe = new NamedPipeServerStream(
                    //     IpcConstants.PipeName + "_status",
                    //     PipeDirection.InOut,
                    //     NamedPipeServerStream.MaxAllowedServerInstances,
                    //     PipeTransmissionMode.Message,
                    //     PipeOptions.Asynchronous);

                    statusPipe = NamedPipeServerStreamAcl.Create(
                        IpcConstants.PipeName + "_status",
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous,
                        0,
                        0,
                        pipeSecurity);

                    //statusPipe.SetAccessControl(pipeSecurity);

                    _logger.LogDebug("Waiting for status listener connection...");
                    await statusPipe.WaitForConnectionAsync(cancellationToken);
                    _logger.LogDebug("Status listener connected");

                    reader = new StreamReader(statusPipe);
                    writer = new StreamWriter(statusPipe) { AutoFlush = true };

                    // Wait for registration message
                    var messageJson = await reader.ReadLineAsync(cancellationToken);
                    var message = JsonSerializer.Deserialize<IpcMessage>(messageJson!);

                    if (message?.Command == "register_status_listener")
                    {
                        _logger.LogDebug("Status listener registered");
                        lock (activeWriters)
                        {
                            activeWriters.Add(writer);
                            _logger.LogDebug("Added status listener to active writers (count: {count})", activeWriters.Count);
                        }

                        // Keep the connection alive until it's broken or cancelled
                        while (statusPipe.IsConnected && !cancellationToken.IsCancellationRequested)
                        {
                            await Task.Delay(1000, cancellationToken);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling status listener");
                }
                finally
                {
                    if (writer != null)
                    {
                        lock (activeWriters)
                        {
                            activeWriters.Remove(writer);
                            _logger.LogDebug("Removed status listener from active writers (count: {count})", activeWriters.Count);
                        }
                    }

                    try
                    {
                        if (writer != null) try { writer.Dispose(); } catch { }
                        if (reader != null) try { reader.Dispose(); } catch { }
                        if (statusPipe != null)
                        {
                            if (statusPipe.IsConnected) try { statusPipe.Disconnect(); } catch { }
                            try { statusPipe.Dispose(); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error during status listener cleanup");
                    }
                }
            }
        }, cancellationToken);

        // Main IPC server loop for handling regular commands
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipeServer = null;
            StreamReader? reader = null;
            StreamWriter? writer = null;

            try
            {
                pipeServer = NamedPipeServerStreamAcl.Create(
                    IpcConstants.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    pipeSecurity);

                //pipeServer.SetAccessControl(pipeSecurity);

                _logger.LogDebug("Waiting for client connection...");
                await pipeServer.WaitForConnectionAsync(cancellationToken);
                _logger.LogDebug("Client connected successfully");

                reader = new StreamReader(pipeServer);
                writer = new StreamWriter(pipeServer) { AutoFlush = true };

                // Add writer to active writers list
                lock (activeWriters)
                {
                    activeWriters.Add(writer);
                    _logger.LogDebug("Added new writer to active writers (count: {count})", activeWriters.Count);
                }

                while (pipeServer.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    var messageJson = await reader.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrEmpty(messageJson))
                    {
                        _logger.LogDebug("Received empty message, checking if client is still connected");
                        if (!pipeServer.IsConnected)
                        {
                            _logger.LogDebug("Client disconnected");
                            break;
                        }
                        continue;
                    }

                    _logger.LogDebug("Received message: {message}", messageJson);

                    var message = JsonSerializer.Deserialize<IpcMessage>(messageJson);
                    if (message == null)
                    {
                        _logger.LogError("Failed to deserialize message");
                        continue;
                    }

                    try
                    {
                        // Process the message and get responses
                        var (mainResponse, statusResponse) = await ProcessIpcMessage(message);

                        if (!pipeServer.IsConnected)
                        {
                            _logger.LogDebug("Client disconnected while processing message");
                            break;
                        }

                        // Send the main response
                        var responseJson = JsonSerializer.Serialize(mainResponse);
                        _logger.LogDebug("Sending main response: {response}", responseJson);
                        await writer.WriteLineAsync(responseJson);
                        await writer.FlushAsync();

                        // If we have a status response, send it too
                        if (statusResponse != null && pipeServer.IsConnected)
                        {
                            responseJson = JsonSerializer.Serialize(statusResponse);
                            _logger.LogDebug("Sending status response: {response}", responseJson);
                            await writer.WriteLineAsync(responseJson);
                            await writer.FlushAsync();

                            // If this was a save operation, add this writer to active writers
                            if (message.Command == IpcConstants.SaveConfigCommand)
                            {
                                lock (activeWriters)
                                {
                                    if (!activeWriters.Contains(writer))
                                    {
                                        _logger.LogDebug("Adding save operation writer to active writers");
                                        activeWriters.Add(writer);
                                    }
                                }
                            }
                        }
                    }
                    catch (IOException ex) when (ex.Message.Contains("Pipe is broken"))
                    {
                        _logger.LogDebug("Client disconnected while sending response");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing message");
                        if (pipeServer.IsConnected)
                        {
                            try
                            {
                                var errorResponse = new IpcResponse
                                {
                                    Success = false,
                                    Error = "Internal server error: " + ex.Message
                                };
                                await writer.WriteLineAsync(JsonSerializer.Serialize(errorResponse));
                                await writer.FlushAsync();
                            }
                            catch (IOException)
                            {
                                _logger.LogDebug("Client disconnected while sending error response");
                                break;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("IPC server shutting down");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling client connection");
                await Task.Delay(100, cancellationToken);
            }
            finally
            {
                // Remove writer from active writers list
                if (writer != null)
                {
                    lock (activeWriters)
                    {
                        activeWriters.Remove(writer);
                        _logger.LogDebug("Removed writer from active writers (count: {count})", activeWriters.Count);
                    }
                }

                try
                {
                    if (writer != null)
                    {
                        try { writer.Dispose(); } catch { }
                    }
                    if (reader != null)
                    {
                        try { reader.Dispose(); } catch { }
                    }
                    if (pipeServer != null)
                    {
                        if (pipeServer.IsConnected)
                        {
                            try { pipeServer.Disconnect(); } catch { }
                        }
                        try { pipeServer.Dispose(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error during cleanup");
                }
            }
        }

        _logger.LogInformation("IPC server shutting down");
    }

    private async Task<(IpcResponse MainResponse, IpcResponse? StatusResponse)> ProcessIpcMessage(IpcMessage message)
    {
        _logger.LogDebug("Processing IPC message: {command}", message.Command);

        switch (message.Command)
        {
            case IpcConstants.GetConfigCommand:
                var connectorConfig = await _configService.GetConfigAsync();
                var config = new ConfigurationMessage
                {
                    Url = connectorConfig.Url,
                    ApiKey = connectorConfig.ApiKey
                };
                var mainResponse = new IpcResponse
                {
                    Success = true,
                    Config = config
                };

                // If we have a URL configured but socket isn't connected yet, show pending
                var isConnected = _socketService?.IsConnected ?? false;
                var isPending = !string.IsNullOrEmpty(connectorConfig.Url) && !isConnected;
                var connectionState = isPending ? "pending" : (isConnected ? "true" : "false");
                _logger.LogInformation("Current socket status: {status}", isPending ? "Pending" : (isConnected ? "Connected" : "Disconnected"));

                var statusResponse = new IpcResponse
                {
                    Success = true,
                    Config = new ConfigurationMessage { Url = connectionState },
                    Command = "connection_status_changed"
                };

                return (mainResponse, statusResponse);

            case IpcConstants.SaveConfigCommand:
                if (message.Parameters == null || message.Parameters.Length == 0)
                {
                    return (new IpcResponse { Success = false, Error = "No configuration provided" }, null);
                }

                try
                {
                    var newConfig = JsonSerializer.Deserialize<ConfigurationMessage>(message.Parameters[0]);
                    if (newConfig == null)
                    {
                        return (new IpcResponse { Success = false, Error = "Invalid configuration format" }, null);
                    }

                    var newConnectorConfig = new ConnectorConfig
                    {
                        Url = newConfig.Url,
                        ApiKey = newConfig.ApiKey
                    };
                    await _configService.SaveConfigAsync(newConnectorConfig);

                    // After saving config, we're in a pending state until socket connects
                    statusResponse = new IpcResponse
                    {
                        Success = true,
                        Config = new ConfigurationMessage { Url = "pending" },
                        Command = "connection_status_changed"
                    };

                    return (new IpcResponse { Success = true }, statusResponse);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving configuration");
                    return (new IpcResponse { Success = false, Error = ex.Message }, null);
                }

            case "get_socket_status":
                isConnected = _socketService?.IsConnected ?? false;
                connectorConfig = await _configService.GetConfigAsync();
                isPending = !string.IsNullOrEmpty(connectorConfig.Url) && !isConnected;
                connectionState = isPending ? "pending" : (isConnected ? "true" : "false");
                _logger.LogInformation("Socket status requested: {status}", isPending ? "Pending" : (isConnected ? "Connected" : "Disconnected"));
                return (new IpcResponse
                {
                    Success = true,
                    Config = new ConfigurationMessage { Url = connectionState }
                }, null);

            default:
                _logger.LogWarning("Unknown command received: {command}", message.Command);
                return (new IpcResponse { Success = false, Error = "Unknown command" }, null);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Unsubscribe from events
        if (_socketService != null)
        {
            _socketService.CertificateAuthoritiesConfigured -= authorities =>
                _pollingService.ConfigurePolling(authorities);
        }

        await base.StopAsync(cancellationToken);
    }
}