using System.Text.RegularExpressions;
using Binace.Trader.Application.Contracts;

namespace Binace.Trader.Infrastructure.Services;

public static partial class TtFileParser
{
    public static IntegrationSettingsDto Parse(IEnumerable<string> rawLines, string sourcePath)
    {
        var lines = rawLines
            .Select(line => line.Trim())
            .ToArray();

        string apiKey = string.Empty;
        string apiSecret = string.Empty;
        string telegramBotToken = string.Empty;
        string zaloToken = string.Empty;
        string webhookUrl = string.Empty;
        var allowedIpAddresses = new List<string>();
        var autoDetectChatId = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (string.IsNullOrEmpty(telegramBotToken) &&
                line.Contains("telegram", StringComparison.OrdinalIgnoreCase) &&
                line.Contains(':'))
            {
                telegramBotToken = line[(line.IndexOf(':') + 1)..].Trim();
                continue;
            }

            if (string.IsNullOrEmpty(zaloToken) &&
                line.Contains("zalo", StringComparison.OrdinalIgnoreCase) &&
                line.Contains(':'))
            {
                zaloToken = line[(line.IndexOf(':') + 1)..].Trim();
                continue;
            }

            if (line.Contains("chat id", StringComparison.OrdinalIgnoreCase))
            {
                autoDetectChatId = true;
                continue;
            }

            if (string.IsNullOrEmpty(apiKey) && line.StartsWith("Key API", StringComparison.OrdinalIgnoreCase))
            {
                apiKey = ReadNextValue(lines, index + 1);
                continue;
            }

            if (string.IsNullOrEmpty(apiSecret) &&
                line.StartsWith("Key", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("API", StringComparison.OrdinalIgnoreCase))
            {
                apiSecret = ReadNextValue(lines, index + 1);
                continue;
            }

            if (string.IsNullOrEmpty(webhookUrl) &&
                Uri.TryCreate(line, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                webhookUrl = line;
                continue;
            }

            if (IpAddressRegex().IsMatch(line))
            {
                allowedIpAddresses.Add(line);
            }
        }

        return new IntegrationSettingsDto(
            ApiKey: apiKey,
            ApiSecret: apiSecret,
            TelegramBotToken: telegramBotToken,
            AutoDetectChatId: autoDetectChatId,
            WebhookUrl: webhookUrl,
            ZaloToken: zaloToken,
            AllowedIpAddresses: allowedIpAddresses,
            LoadedFromFile: !string.IsNullOrWhiteSpace(sourcePath),
            SourcePath: sourcePath);
    }

    private static string ReadNextValue(string[] lines, int startIndex)
    {
        for (var index = startIndex; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("Key", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            return line.Trim();
        }

        return string.Empty;
    }

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$")]
    private static partial Regex IpAddressRegex();
}
