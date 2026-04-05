using System.Threading;
using System.Threading.Tasks;

namespace Binace.Trader.Application.Abstractions;

public interface INotificationService
{
    Task SendNotificationAsync(string message, CancellationToken cancellationToken);
    Task TestZaloAsync(string token, CancellationToken cancellationToken);
}
