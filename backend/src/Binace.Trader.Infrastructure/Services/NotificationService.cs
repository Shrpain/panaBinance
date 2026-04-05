using Binace.Trader.Application.Abstractions;
using Binace.Trader.Application.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Binace.Trader.Infrastructure.Services;

public sealed class NotificationService : INotificationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITradingDashboardService _dashboardService;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        IHttpClientFactory httpClientFactory,
        ITradingDashboardService dashboardService,
        ILogger<NotificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _dashboardService = dashboardService;
        _logger = logger;
    }

    public async Task SendNotificationAsync(string message, CancellationToken cancellationToken)
    {
        var settings = await _dashboardService.GetIntegrationSettingsAsync(cancellationToken);
        
        // Zalo
        if (!string.IsNullOrWhiteSpace(settings.ZaloToken))
        {
            await SendZaloInternalAsync(settings.ZaloToken, message, cancellationToken);
        }

        // Telegram (Simplified - would need chat_id)
        if (!string.IsNullOrWhiteSpace(settings.TelegramBotToken))
        {
            // For now, we skip Telegram unless we have a chat_id. 
            // The user specifically asked for Zalo.
        }
    }

    public async Task TestZaloAsync(string token, CancellationToken cancellationToken)
    {
        await SendZaloInternalAsync(token, "🔔 Đây là tin nhắn thử nghiệm từ Bot Trading!", cancellationToken);
    }

    private async Task SendZaloInternalAsync(string combinedToken, string message, CancellationToken cancellationToken)
    {
        try
        {
            var parts = combinedToken.Split(':');
            if (parts.Length < 2)
            {
                _logger.LogWarning("Invalid Zalo token format. Expected ID:Token or ID:Token:To.");
                return;
            }

            var id = parts[0];
            var token = parts[1];
            var to = parts.Length > 2 ? parts[2] : "";
            
            var encodedMessage = HttpUtility.UrlEncode(message);
            var url = $"https://api.puz.vn/zalo/send?id={id}&token={token}&message={encodedMessage}";
            if (!string.IsNullOrWhiteSpace(to))
            {
                url += $"&to={to}";
            }

            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to send Zalo notification. HTTP {Code}: {Error}", response.StatusCode, content);
            }
            else
            {
                _logger.LogInformation("Zalo API response: {Content}", content);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending Zalo notification");
        }
    }
}
