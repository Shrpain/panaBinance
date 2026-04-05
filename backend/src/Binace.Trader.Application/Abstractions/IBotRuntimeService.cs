using Binace.Trader.Application.Contracts;

namespace Binace.Trader.Application.Abstractions;

public interface IBotRuntimeService
{
    BotRuntimeStateDto GetState();

    BotRuntimeStateDto AddTraderCoin(string symbol);

    BotRuntimeStateDto RemoveTraderCoin(string symbol);

    BotRuntimeStateDto UpdateSettings(UpdateBotRuntimeSettingsRequestDto request);

    BotRuntimeStateDto Start();

    BotRuntimeStateDto Stop();
}
