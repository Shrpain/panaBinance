namespace Binace.Trader.Application.Contracts;

public sealed record UpdateBotConfigurationRequest(
    string Symbol,
    string Strategy,
    decimal RiskPerTradePercent,
    bool PaperTradingEnabled);
