namespace Binace.Trader.Application.Contracts;

public sealed record IntegrationSettingsDto(
    string ApiKey,
    string ApiSecret,
    string TelegramBotToken,
    bool AutoDetectChatId,
    string WebhookUrl,
    string ZaloToken,
    IReadOnlyList<string> AllowedIpAddresses,
    bool LoadedFromFile,
    string SourcePath);
