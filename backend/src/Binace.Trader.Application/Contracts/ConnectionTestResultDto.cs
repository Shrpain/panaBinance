namespace Binace.Trader.Application.Contracts;

public sealed record ConnectionTestResultDto(
    string Service,
    bool Success,
    string Message,
    string Details,
    DateTimeOffset CheckedAtUtc);
