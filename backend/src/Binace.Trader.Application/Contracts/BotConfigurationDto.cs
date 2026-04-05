namespace Binace.Trader.Application.Contracts;

public sealed record BotConfigurationDto(
    string Exchange,
    string Symbol,
    string Strategy,
    decimal RiskPerTradePercent,
    bool PaperTradingEnabled);
