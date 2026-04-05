using Binace.Trader.Application.Contracts;

namespace Binace.Trader.Application.Abstractions;

public interface IBotTerminalEventService
{
    IReadOnlyList<BotTerminalEventDto> GetRecentEvents();

    BotTerminalEventDto Add(string level, string message);
}
