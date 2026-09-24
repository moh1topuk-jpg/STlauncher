using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Diagnostics;
using Xunit;

namespace STlauncher.Core.Tests;

public class NetworkDiagnosticsTests
{
    private sealed class Canned : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _mediaType;

        public Canned(HttpStatusCode status, string mediaType)
        {
            _status = status;
            _mediaType = mediaType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status) { Content = new StringContent("<html>blocked</html>") };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_mediaType);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ProviderBlockPage_FailsAHostThatMustNotAnswerWithHtml()
    {
        var diagnostics = new NetworkDiagnostics(new HttpClient(new Canned(HttpStatusCode.OK, "text/html")));

        var result = await diagnostics.ProbeAsync(new NetworkTarget("session", "https://sessionserver.mojang.com/x", RejectHtml: true));

        Assert.False(result.Ok);
        Assert.Equal("html-stub", result.Error);
    }

    [Fact]
    public async Task RealAnswer_PassesEvenWhenTheProbePathIsRejected()
    {
        var diagnostics = new NetworkDiagnostics(new HttpClient(new Canned(HttpStatusCode.BadRequest, "text/plain")));

        var result = await diagnostics.ProbeAsync(new NetworkTarget("textures", "https://textures.minecraft.net/", RejectHtml: true));

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task HtmlIsFineForHostsThatServePages()
    {
        var diagnostics = new NetworkDiagnostics(new HttpClient(new Canned(HttpStatusCode.OK, "text/html")));

        var result = await diagnostics.ProbeAsync(new NetworkTarget("github", "https://github.com/"));

        Assert.True(result.Ok);
    }
}
