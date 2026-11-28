using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using MudBlazorCrmApp.Services.Agent;
using Xunit;

namespace MudBlazorCrmApp.Tests;

public class AgentLoopbackTests
{
    [Fact]
    public void ResolveLoopbackBase_UsesRequestSchemeAndConnectionPort_NotServerBindList()
    {
        // The multi-bind bug: with two https ports the OLD code picked the first same-scheme bind and
        // self-called the WRONG listener. The connection's own port is the only guaranteed one.
        Assert.Equal("https://127.0.0.1:7031", AgentDataService.ResolveLoopbackBase(null, "https", 7031));
        Assert.Equal("https://127.0.0.1:5026", AgentDataService.ResolveLoopbackBase(null, "https", 5026));
        Assert.Equal("http://127.0.0.1:5025", AgentDataService.ResolveLoopbackBase(null, "http", 5025));
        // Unknown/absent port falls back to the dev-profile port; odd schemes are treated as http.
        Assert.Equal("http://127.0.0.1:5025", AgentDataService.ResolveLoopbackBase(null, "http", 0));
        Assert.Equal("http://127.0.0.1:8080", AgentDataService.ResolveLoopbackBase(null, "weird", 8080));
    }

    [Fact]
    public void ResolveLoopbackBase_ConfiguredOverride_MustBePlainLoopbackHttp()
    {
        Assert.Equal("http://127.0.0.1:5000", AgentDataService.ResolveLoopbackBase("http://127.0.0.1:5000", "https", 5026));
        Assert.Equal("https://localhost:7031", AgentDataService.ResolveLoopbackBase("https://localhost:7031/", "http", 5025));

        // Non-loopback, userinfo-bearing, wrong-scheme and garbage values are ignored (fall back).
        foreach (var bad in new[] { "http://evil.example:5000", "http://u:p@127.0.0.1:5000", "file:///x", "not-a-uri", "" })
            Assert.Equal("https://127.0.0.1:5026", AgentDataService.ResolveLoopbackBase(bad, "https", 5026));
    }

    [Fact]
    public void LoopbackCertGate_AcceptsOnlyLoopbackTargetsWithACert()
    {
        using var cert = new X509Certificate2();
        Assert.True(AgentLoopbackPolicy.AcceptLoopbackCertificate(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:5026/x"), cert));
        Assert.True(AgentLoopbackPolicy.AcceptLoopbackCertificate(new HttpRequestMessage(HttpMethod.Get, "https://[::1]:5026/x"), cert));
        Assert.True(AgentLoopbackPolicy.AcceptLoopbackCertificate(new HttpRequestMessage(HttpMethod.Get, "https://localhost:5026/x"), cert));

        // Fail closed: any non-loopback target, missing cert, or missing request.
        Assert.False(AgentLoopbackPolicy.AcceptLoopbackCertificate(new HttpRequestMessage(HttpMethod.Get, "https://evil.example/x"), cert));
        Assert.False(AgentLoopbackPolicy.AcceptLoopbackCertificate(new HttpRequestMessage(HttpMethod.Get, "https://127.0.0.1.evil.example/x"), cert));
        Assert.False(AgentLoopbackPolicy.AcceptLoopbackCertificate(new HttpRequestMessage(HttpMethod.Get, "https://127.0.0.1:5026/x"), null));
        Assert.False(AgentLoopbackPolicy.AcceptLoopbackCertificate(null, cert));
    }
}
