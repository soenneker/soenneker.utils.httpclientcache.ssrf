using Soenneker.Dtos.HttpClientOptions;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Validators.IpAddresses.Ssrf.Abstract;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Utils.HttpClientCache.Ssrf;

internal sealed class SsrfHttpClientHandler : HttpClientHandler
{
    private static readonly TimeSpan _defaultConnectTimeout = TimeSpan.FromSeconds(100);
    private static readonly TimeSpan _defaultPooledConnectionLifetime = TimeSpan.FromMinutes(10);

    private readonly HttpMessageInvoker _invoker;
    private readonly ISsrfIpAddressValidator _ipAddressValidator;

    public SsrfHttpClientHandler(ISsrfIpAddressValidator ipAddressValidator, HttpClientOptions? options)
    {
        _ipAddressValidator = ipAddressValidator;

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = Connect,
            ConnectTimeout = options?.ConnectTimeout ?? _defaultConnectTimeout,
            MaxConnectionsPerServer = options?.MaxConnectionsPerServer ?? 40,
            PooledConnectionLifetime = options?.PooledConnectionLifetime ?? _defaultPooledConnectionLifetime,
            UseCookies = options?.UseCookieContainer == true,
            UseProxy = false
        };

        if (options?.UseCookieContainer == true)
            handler.CookieContainer = new CookieContainer();

        if (options?.ResponseDrainTimeout is { } responseDrainTimeout)
            handler.ResponseDrainTimeout = responseDrainTimeout;

        if (options?.AllowAutoRedirect is { } allowAutoRedirect)
            handler.AllowAutoRedirect = allowAutoRedirect;

        if (options?.AutomaticDecompression is { } automaticDecompression)
            handler.AutomaticDecompression = automaticDecompression;

        if (options?.KeepAlivePingDelay is { } keepAlivePingDelay)
            handler.KeepAlivePingDelay = keepAlivePingDelay;

        if (options?.KeepAlivePingTimeout is { } keepAlivePingTimeout)
            handler.KeepAlivePingTimeout = keepAlivePingTimeout;

        if (options?.KeepAlivePingPolicy is { } keepAlivePingPolicy)
            handler.KeepAlivePingPolicy = keepAlivePingPolicy;

        if (options?.MaxResponseDrainSize is { } maxResponseDrainSize)
            handler.MaxResponseDrainSize = maxResponseDrainSize;

        if (options?.MaxResponseHeadersLength is { } maxResponseHeadersLength)
            handler.MaxResponseHeadersLength = maxResponseHeadersLength;

        _invoker = new HttpMessageInvoker(handler, disposeHandler: true);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _invoker.Send(request, cancellationToken);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _invoker.SendAsync(request, cancellationToken);

    private async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        DnsEndPoint endpoint = context.DnsEndPoint;
        IPAddress[] addresses;

        if (IPAddress.TryParse(endpoint.Host, out IPAddress? literalAddress))
            addresses = [literalAddress];
        else
            addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).NoSync();

        if (addresses.Length == 0)
            throw new HttpRequestException($"Host '{endpoint.Host}' did not resolve to an IP address.");

        foreach (IPAddress address in addresses)
        {
            if (!_ipAddressValidator.Validate(address))
                throw new HttpRequestException($"Connections to non-public IP address '{address}' are blocked.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, cancellationToken).NoSync();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _invoker.Dispose();

        base.Dispose(disposing);
    }
}
