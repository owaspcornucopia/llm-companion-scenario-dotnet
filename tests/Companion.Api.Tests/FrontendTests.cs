using System.Net;
using System.Net.Http;
using Xunit;

namespace Companion.Api.Tests;

public sealed class FrontendTests
{
    [Fact]
    public async Task RootServesTheTransactionReviewFrontend()
    {
        using var factory = new CompanionApiFactory("app");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Review payments with confidence.", html);
        Assert.Contains("Review status: waiting for an investigation.", html);
        Assert.Contains("data-question=\"Show all transactions\"", html);
        Assert.Contains("<script src=\"app.js\" defer></script>", html);
    }

    [Fact]
    public async Task FrontendAssetsAreServed()
    {
        using var factory = new CompanionApiFactory("app");
        using var client = factory.CreateClient();

        var scriptResponse = await client.GetAsync("/app.js");
        var stylesheetResponse = await client.GetAsync("/styles.css");

        Assert.Equal(HttpStatusCode.OK, scriptResponse.StatusCode);
        var script = await scriptResponse.Content.ReadAsStringAsync();
        Assert.Contains("AbortController", script);
        Assert.Contains("sampleLinks.forEach", script);
        Assert.Equal(HttpStatusCode.OK, stylesheetResponse.StatusCode);
        Assert.Contains("--canvas", await stylesheetResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RootFormSubmissionRedirectsBackToTheFrontend()
    {
        using var factory = new CompanionApiFactory("app");
        using var client = factory.CreateClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["question"] = "Check transaction TX-1002",
        });

        var response = await client.PostAsync("/", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Review payments with confidence.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReportEndpointReturnsDownloadablePlainText()
    {
        using var factory = new CompanionApiFactory("app");
        using var client = factory.CreateClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["question"] = "Check transaction TX-1002",
            ["verdict"] = "Investigation complete",
            ["answer"] = "transaction answer",
        });

        var response = await client.PostAsync("/report", content);
        var report = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("Investigation summary:\ntransaction answer", report);
    }
}
