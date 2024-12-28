using Microsoft.Extensions.Logging;
using Shared.Services;
using System;
using System.Threading.Tasks;

namespace Service.Services
{
    public class SocketMessageService : ISocketMessageService
    {
        private readonly SocketService _socketService;
        private readonly ILogger<SocketMessageService> _logger;

        public bool IsConnected => _socketService.IsConnected;

        public SocketMessageService(SocketService socketService, ILogger<SocketMessageService> logger)
        {
            _socketService = socketService;
            _logger = logger;
        }

        public async Task SendMessageAsync(string type, object data)
        {
            try
            {
                if (!IsConnected)
                {
                    _logger.LogDebug("Socket not connected, skipping message: {type}", type);
                    return;
                }

                await Task.Run(async () =>
                {
                    try
                    {
                        await _socketService.EmitMessageAsync(SocketMessage.CONNECTOR_TO_API, new
                        {
                            type,
                            data
                        });
                        _logger.LogDebug("Message sent: {type}", type);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error sending message: {type}", type);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in message handler");
            }
        }
    }
}