namespace Binace.Trader.Application.Contracts;

public sealed record OpenPositionDto(
    string Symbol,
    string Side,
    decimal EntryPrice,
    decimal MarkPrice,
    decimal Quantity,
    decimal Pnl,
    decimal? StopLoss = null,
    decimal? TakeProfit = null);
