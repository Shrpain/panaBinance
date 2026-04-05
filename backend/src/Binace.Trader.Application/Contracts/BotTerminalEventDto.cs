namespace Binace.Trader.Application.Contracts;

public sealed record BotTerminalEventDto(
    long Id,
    DateTimeOffset TimeUtc,
    string Level,
    string Message);
