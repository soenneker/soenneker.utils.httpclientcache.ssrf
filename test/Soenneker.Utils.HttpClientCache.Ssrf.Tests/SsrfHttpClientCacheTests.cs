using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Soenneker.Dtos.HttpClientOptions;
using Soenneker.Tests.HostedUnit;
using Soenneker.Utils.HttpClientCache.Ssrf.Abstract;

namespace Soenneker.Utils.HttpClientCache.Ssrf.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class SsrfHttpClientCacheTests : HostedUnitTest
{
    private readonly ISsrfHttpClientCache _cache;

    public SsrfHttpClientCacheTests(Host host) : base(host)
    {
        _cache = Resolve<ISsrfHttpClientCache>(true);
    }

    [Test]
    public async Task Get_should_cache_client(CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString();

        HttpClient first = await _cache.Get(id, cancellationToken);
        HttpClient second = await _cache.Get(id, cancellationToken);

        second.Should().BeSameAs(first);
    }

    [Test]
    public async Task Options_should_be_applied(CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString();
        TimeSpan timeout = TimeSpan.FromSeconds(17);

        HttpClient client = await _cache.Get(id, () => new HttpClientOptions { Timeout = timeout }, cancellationToken);

        client.Timeout.Should().Be(timeout);
    }

    [Test]
    public async Task Remove_should_allow_client_to_be_recreated(CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString();
        HttpClient first = await _cache.Get(id, cancellationToken);

        await _cache.Remove(id);
        HttpClient second = await _cache.Get(id, cancellationToken);

        second.Should().NotBeSameAs(first);
    }

    [Test]
    [Arguments("127.0.0.1")]
    [Arguments("10.0.0.1")]
    [Arguments("169.254.169.254")]
    [Arguments("192.168.1.1")]
    [Arguments("::1")]
    [Arguments("fc00::1")]
    [Arguments("localhost")]
    public async Task Requests_to_non_public_addresses_should_be_blocked(string host, CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString();
        HttpClient client = await _cache.Get(id, cancellationToken);
        var uri = new UriBuilder(Uri.UriSchemeHttp, host).Uri;

        Func<Task> request = () => client.GetAsync(uri, cancellationToken);

        await request.Should().ThrowAsync<HttpRequestException>()
                     .WithMessage("*blocked*");
    }

    [Test]
    public async Task Proxy_configuration_should_be_rejected(CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString();

        Func<Task> get = async () => await _cache.Get(id, static () => new HttpClientOptions { UseProxy = true }, cancellationToken);

        await get.Should().ThrowAsync<NotSupportedException>();
    }

    [Test]
    public async Task Custom_ssl_options_should_be_rejected(CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString();

        Func<Task> get = async () => await _cache.Get(id, static () => new HttpClientOptions { SslOptions = new() }, cancellationToken);

        await get.Should().ThrowAsync<NotSupportedException>();
    }

}
