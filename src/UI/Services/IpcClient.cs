using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shared;

namespace UI.Services
{
    public enum ConnectionState
    {
        Pending,
        Connected,
        Disconnected
    }

    public class IpcClient : IDisposable
    {
        public event Action<ConnectionState>? ConnectionStatusChanged;
        private ConnectionState _connectionState = ConnectionState.Pending;
        private ConnectionState ConnectionState
        {
            get => _connectionState;
            set
            {
                if (_connectionState != value)
                {
                    _connectionState = value;
                    ConnectionStatusChanged?.Invoke(_connectionState);
                }
            }
        }

        private readonly CancellationTokenSource _cts = new();
        private Task? _listenerTask;
        private Task? _statusListenerTask;

        public Task StartListening(Action showWindow)
        {
            // Start the status update listener in a separate task
            _statusListenerTask = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        using var pipeClient = new NamedPipeClientStream(
                            ".", 
                            IpcConstants.PipeName + "_status",
                            PipeDirection.InOut,
                            PipeOptions.None);

                        Console.WriteLine("Status listener connecting to pipe...");
                        await pipeClient.ConnectAsync(_cts.Token);
                        
                        if (pipeClient.IsConnected)
                        {
                            Console.WriteLine("Status listener connected successfully");
                            using var reader = new StreamReader(pipeClient);
                            using var writer = new StreamWriter(pipeClient) { AutoFlush = true };

                            // Send a message to register this as a status listener
                            var registerMessage = new IpcMessage { Command = "register_status_listener" };
                            var registerJson = JsonSerializer.Serialize(registerMessage);
                            await writer.WriteLineAsync(registerJson);
                            await writer.FlushAsync();

                            // Keep reading from the pipe for status updates
                            while (pipeClient.IsConnected && !_cts.Token.IsCancellationRequested)
                            {
                                var statusJson = await reader.ReadLineAsync();
                                if (!string.IsNullOrEmpty(statusJson))
                                {
                                    Console.WriteLine($"Received status update: {statusJson}");
                                    var response = JsonSerializer.Deserialize<IpcResponse>(statusJson);
                                    if (response?.Command == "connection_status_changed" && response.Config?.Url != null)
                                    {
                                        var status = response.Config.Url.ToLower();
                                        switch (status)
                                        {
                                            case "true":
                                                ConnectionState = ConnectionState.Connected;
                                                break;
                                            case "pending":
                                                ConnectionState = ConnectionState.Pending;
                                                break;
                                            case "false":
                                                ConnectionState = ConnectionState.Disconnected;
                                                break;
                                            default:
                                                Console.WriteLine($"Unexpected status value: {status}");
                                                ConnectionState = ConnectionState.Disconnected;
                                                break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Status listener error: {ex.Message}");
                        await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                    }
                }
            }, _cts.Token);

            // Start the regular polling task
            _listenerTask = Task.Run(async () =>
            {
                bool needsConfig = false;
                ConnectionState = ConnectionState.Pending;

                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        // Only check configuration if we haven't determined the state
                        // or if we know configuration is needed
                        if (!needsConfig)
                        {
                            // Try to get the configuration
                            var config = await GetConfiguration();
                            if (config == null)
                            {
                                // If we couldn't get the configuration, wait before retrying
                                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                                continue;
                            }

                            if (string.IsNullOrEmpty(config.Url))
                            {
                                needsConfig = true;
                                ConnectionState = ConnectionState.Disconnected;
                                Console.WriteLine("Configuration needed - showing window");
                                showWindow();
                            }
                            else
                            {
                                needsConfig = false;
                                // Get socket connection status from service
                                await GetSocketStatus();
                                // ConnectionState is already set by GetSocketStatus()
                                Console.WriteLine($"Socket connection status: {ConnectionState}");
                                // Check status less frequently when we have a persistent listener
                                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                            }
                        }
                        else
                        {
                            // Wait between checks when we need configuration
                            await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        ConnectionState = ConnectionState.Disconnected;
                        Console.WriteLine($"Listener error: {ex.Message}");
                        await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                    }
                }
            }, _cts.Token);

            return Task.WhenAll(_listenerTask, _statusListenerTask);
        }

        public async Task<ConfigurationMessage?> GetConfiguration()
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    Console.WriteLine($"Getting configuration (attempt {attempt}/3)...");
                    using var pipeClient = new NamedPipeClientStream(
                        ".", 
                        IpcConstants.PipeName,
                        PipeDirection.InOut,
                        PipeOptions.None);

                    Console.WriteLine("Attempting to connect...");
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await pipeClient.ConnectAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("Connection attempt timed out");
                        continue;
                    }

                    if (!pipeClient.IsConnected)
                    {
                        Console.WriteLine("Failed to establish connection");
                        continue;
                    }

                    Console.WriteLine("Connected to service");
                    using var reader = new StreamReader(pipeClient);
                    using var writer = new StreamWriter(pipeClient) { AutoFlush = true };

                    var message = new IpcMessage { Command = IpcConstants.GetConfigCommand };
                    var messageJson = JsonSerializer.Serialize(message);
                    Console.WriteLine($"Sending message: {messageJson}");
                    await writer.WriteLineAsync(messageJson);
                    await writer.FlushAsync();

                    // Read the configuration response with timeout
                    cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var responseJson = await ReadLineWithTimeoutAsync(reader, cts.Token);
                    Console.WriteLine($"Received response: {responseJson}");

                    if (string.IsNullOrEmpty(responseJson))
                    {
                        Console.WriteLine("Empty response received");
                        continue;
                    }

                    var response = JsonSerializer.Deserialize<IpcResponse>(responseJson);
                    if (response?.Success == true)
                    {
                        // Try to read the status response if available
                        try
                        {
                            cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                            var statusJson = await ReadLineWithTimeoutAsync(reader, cts.Token);
                            if (!string.IsNullOrEmpty(statusJson))
                            {
                                var statusResponse = JsonSerializer.Deserialize<IpcResponse>(statusJson);
                                if (statusResponse?.Command == "connection_status_changed" && statusResponse.Config?.Url != null)
                                {
                                    var status = statusResponse.Config.Url.ToLower();
                                    switch (status)
                                    {
                                        case "true":
                                            ConnectionState = ConnectionState.Connected;
                                            break;
                                        case "pending":
                                            ConnectionState = ConnectionState.Pending;
                                            break;
                                        case "false":
                                            ConnectionState = ConnectionState.Disconnected;
                                            break;
                                        default:
                                            Console.WriteLine($"Unexpected status value in GetConfiguration: {status}");
                                            ConnectionState = ConnectionState.Disconnected;
                                            break;
                                    }
                                    Console.WriteLine($"Updated connection status from GetConfiguration: {ConnectionState}");
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            Console.WriteLine("Status response read timed out");
                            // Non-fatal error, we still have the configuration
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error reading status response: {ex.Message}");
                            // Non-fatal error, we still have the configuration
                        }

                        return response.Config;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Get configuration attempt {attempt} failed: {ex.Message}");
                }

                if (attempt < 3)
                {
                    await Task.Delay(500);
                }
            }

            return null;
        }

        private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<string?>();
            var readTask = Task.Run(async () =>
            {
                try
                {
                    return await reader.ReadLineAsync();
                }
                catch (Exception)
                {
                    return null;
                }
            });

            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
            var completedTask = await Task.WhenAny(readTask, tcs.Task);
            
            if (completedTask == readTask)
            {
                return await readTask;
            }

            throw new OperationCanceledException("Read operation timed out", cancellationToken);
        }

        private async Task<bool> IsServiceRunning()
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    Console.WriteLine($"Checking if service is running (attempt {attempt}/3)...");
                    using var pipeClient = new NamedPipeClientStream(
                        ".", 
                        IpcConstants.PipeName,
                        PipeDirection.InOut,
                        PipeOptions.None);
                    
                    Console.WriteLine("Attempting to connect to service...");
                    await pipeClient.ConnectAsync(2000);
                    
                    if (pipeClient.IsConnected)
                    {
                        Console.WriteLine("Successfully connected to service");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Service check attempt {attempt} failed: {ex.Message}");
                    if (attempt < 3)
                    {
                        await Task.Delay(500);
                    }
                }
            }
            
            Console.WriteLine("Service check failed after 3 attempts");
            return false;
        }

        public async Task<bool> SaveConfiguration(string url, string apiKey)
        {
            Console.WriteLine("Starting save configuration...");
            ConnectionState = ConnectionState.Pending;

            if (!await IsServiceRunning())
            {
                throw new Exception("Service is not running. Please start the Certificate Manager Service.");
            }

            try
            {
                Console.WriteLine("Connecting to service for save operation...");
                using var pipeClient = new NamedPipeClientStream(
                    ".", 
                    IpcConstants.PipeName,
                    PipeDirection.InOut,
                    PipeOptions.None);

                Console.WriteLine("Attempting to connect...");
                await pipeClient.ConnectAsync(10000);

                if (!pipeClient.IsConnected)
                {
                    throw new InvalidOperationException("Failed to establish connection");
                }

                Console.WriteLine("Connected to service, sending configuration...");

                using var reader = new StreamReader(pipeClient);
                using var writer = new StreamWriter(pipeClient) { AutoFlush = true };

                var config = new ConfigurationMessage { Url = url, ApiKey = apiKey };
                var message = new IpcMessage 
                { 
                    Command = IpcConstants.SaveConfigCommand,
                    Parameters = new[] { JsonSerializer.Serialize(config) }
                };

                var messageJson = JsonSerializer.Serialize(message);
                Console.WriteLine($"Sending message: {messageJson}");

                await writer.WriteLineAsync(messageJson);
                await writer.FlushAsync();
                Console.WriteLine("Message sent, waiting for response...");

                var responseJson = await reader.ReadLineAsync();
                Console.WriteLine($"Received response: {responseJson}");

                if (string.IsNullOrEmpty(responseJson))
                {
                    throw new InvalidOperationException("No response received from service");
                }

                var response = JsonSerializer.Deserialize<IpcResponse>(responseJson);
                if (response?.Success == true)
                {
                    Console.WriteLine("Configuration saved successfully");
                    
                    // Try to read the status response that comes with saving
                    try
                    {
                        var statusJson = await reader.ReadLineAsync();
                        Console.WriteLine($"Received status response: {statusJson}");
                        if (!string.IsNullOrEmpty(statusJson))
                        {
                            var statusResponse = JsonSerializer.Deserialize<IpcResponse>(statusJson);
                            if (statusResponse?.Command == "connection_status_changed" && statusResponse.Config?.Url != null)
                            {
                                var status = statusResponse.Config.Url.ToLower();
                                switch (status)
                                {
                                    case "true":
                                        ConnectionState = ConnectionState.Connected;
                                        break;
                                    case "pending":
                                        ConnectionState = ConnectionState.Pending;
                                        break;
                                    case "false":
                                        ConnectionState = ConnectionState.Disconnected;
                                        break;
                                    default:
                                        Console.WriteLine($"Unexpected status value in SaveConfiguration: {status}");
                                        ConnectionState = ConnectionState.Disconnected;
                                        break;
                                }
                                Console.WriteLine($"Updated connection status from save response: {ConnectionState}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error reading status response: {ex.Message}");
                    }

                    // Immediately check status after saving
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Check status immediately
                            await GetSocketStatus();

                            // Then do rapid polling for 30 seconds
                            var endTime = DateTime.UtcNow.AddSeconds(30);
                            while (DateTime.UtcNow < endTime && !_cts.Token.IsCancellationRequested)
                            {
                                await Task.Delay(2000, _cts.Token); // Poll every 2 seconds
                                await GetSocketStatus();
                                
                                // If we're already connected, stop rapid polling
                                if (ConnectionState == ConnectionState.Connected)
                                {
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error during rapid status polling: {ex.Message}");
                        }
                    }, _cts.Token);

                    return true;
                }
                else
                {
                    Console.WriteLine($"Failed to save configuration: {response?.Error ?? "Unknown error"}");
                    ConnectionState = ConnectionState.Disconnected;
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Save operation failed: {ex.Message}");
                Console.WriteLine($"Exception type: {ex.GetType().Name}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                ConnectionState = ConnectionState.Disconnected;
                throw;
            }
        }

        private async Task<bool> GetSocketStatus()
        {
            try
            {
                Console.WriteLine("Checking socket status...");
                using var pipeClient = new NamedPipeClientStream(
                    ".", 
                    IpcConstants.PipeName,
                    PipeDirection.InOut,
                    PipeOptions.None);

                await pipeClient.ConnectAsync(2000);
                
                if (pipeClient.IsConnected)
                {
                    using var reader = new StreamReader(pipeClient);
                    using var writer = new StreamWriter(pipeClient) { AutoFlush = true };

                    var message = new IpcMessage { Command = "get_socket_status" };
                    var messageJson = JsonSerializer.Serialize(message);
                    Console.WriteLine($"Sending socket status request: {messageJson}");
                    await writer.WriteLineAsync(messageJson);
                    await writer.FlushAsync();

                    var responseJson = await reader.ReadLineAsync();
                    Console.WriteLine($"Received socket status response: {responseJson}");
                    
                    if (!string.IsNullOrEmpty(responseJson))
                    {
                        var response = JsonSerializer.Deserialize<IpcResponse>(responseJson);
                        if (response?.Success == true && response.Config?.Url != null)
                        {
                            var status = response.Config.Url.ToLower();
                            switch (status)
                            {
                                case "true":
                                    ConnectionState = ConnectionState.Connected;
                                    return true;
                                case "pending":
                                    ConnectionState = ConnectionState.Pending;
                                    return false;
                                case "false":
                                    ConnectionState = ConnectionState.Disconnected;
                                    return false;
                                default:
                                    Console.WriteLine($"Unexpected socket status value: {status}");
                                    ConnectionState = ConnectionState.Disconnected;
                                    return false;
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Invalid socket status response: Success={response?.Success}, Config.Url={response?.Config?.Url}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("Empty response received from socket status request");
                    }
                }
                else
                {
                    Console.WriteLine("Failed to connect to IPC server for socket status check");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking socket status: {ex.GetType().Name} - {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
            ConnectionState = ConnectionState.Disconnected;
            return false;
        }

        public void Dispose()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
            }

            if (_listenerTask != null)
            {
                try
                {
                    _listenerTask.Wait(1000);
                }
                catch { }
                _listenerTask = null;
            }

            if (_statusListenerTask != null)
            {
                try
                {
                    _statusListenerTask.Wait(1000);
                }
                catch { }
                _statusListenerTask = null;
            }
        }
    }
} 