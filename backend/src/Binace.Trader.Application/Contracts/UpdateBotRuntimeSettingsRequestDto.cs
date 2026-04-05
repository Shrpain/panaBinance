namespace Binace.Trader.Application.Contracts;

public sealed record UpdateBotRuntimeSettingsRequestDto(
    string Interval,
    int Leverage,
    decimal Margin,
    string Strategy);
