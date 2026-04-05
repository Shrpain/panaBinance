using System.Net;
using System.Net.Http.Json;
using Binace.Trader.Application.Contracts;
using Binace.Trader.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Binace.Trader.Api.Tests;

public sealed class ApiSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiSmokeTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsOk()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DashboardStatusEndpoint_ReturnsSnapshot()
    {
        using var client = _factory.CreateClient();

        var snapshot = await client.GetFromJsonAsync<DashboardSnapshotDto>("/api/dashboard/status");

        Assert.NotNull(snapshot);
        Assert.Equal("Binance", snapshot.Exchange);
        Assert.True(snapshot.OpenPositions >= 0);
    }

    [Fact]
    public void Parser_Extracts_IntegrationSettings_From_TtFile_Text()
    {
        var lines = new[]
        {
            "* KHONG BAO GIO XOA HAY THAY DOI FILE NAY",
            "token telegram : 123456:abc-token",
            "- tu dong lay chat id",
            "Key API:",
            "api-key-value",
            "Key bi mat:",
            "secret-value",
            "",
            "14.236.50.6",
            "https://example.com/webhook",
        };

        var parsed = TtFileParser.Parse(lines, "C:\\binace\\tt.txt");

        Assert.Equal("123456:abc-token", parsed.TelegramBotToken);
        Assert.Equal("api-key-value", parsed.ApiKey);
        Assert.Equal("secret-value", parsed.ApiSecret);
        Assert.True(parsed.AutoDetectChatId);
        Assert.Equal("https://example.com/webhook", parsed.WebhookUrl);
        Assert.Contains("14.236.50.6", parsed.AllowedIpAddresses);
    }
}
