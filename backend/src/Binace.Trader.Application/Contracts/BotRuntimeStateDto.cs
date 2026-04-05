namespace Binace.Trader.Application.Contracts;

public sealed record BotRuntimeStateDto(
    bool IsRunning,
    string Interval,
    int Leverage,
    decimal Margin,
    string Strategy,
    IReadOnlyList<string> TraderSymbols);
