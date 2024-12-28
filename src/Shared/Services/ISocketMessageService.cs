using System.Threading.Tasks;

namespace Shared.Services
{
    public interface ISocketMessageService
    {
        Task SendMessageAsync(string type, object data);
        bool IsConnected { get; }
    }
} 