using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Atomics.ValueBools;
using Soenneker.Dtos.HttpClientOptions;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.HttpClientCache.Abstract;
using Soenneker.Utils.HttpClientCache.Ssrf.Abstract;
using Soenneker.Validators.IpAddresses.Ssrf.Abstract;

namespace Soenneker.Utils.HttpClientCache.Ssrf;

/// <inheritdoc cref="ISsrfHttpClientCache"/>
public sealed class SsrfHttpClientCache : ISsrfHttpClientCache
{
    private readonly IHttpClientCache _httpClientCache;
    private readonly ISsrfIpAddressValidator _ipAddressValidator;
    private readonly ConcurrentDictionary<string, byte> _clientIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _cacheKeys = new(StringComparer.Ordinal);
    private readonly string _keyPrefix = $"{typeof(SsrfHttpClientCache).FullName}:{Guid.NewGuid():N}:";

    private ValueAtomicBool _disposed;

    public SsrfHttpClientCache(IHttpClientCache httpClientCache, ISsrfIpAddressValidator ipAddressValidator)
    {
        _httpClientCache = httpClientCache;
        _ipAddressValidator = ipAddressValidator;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<HttpClient> Get(string id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _httpClientCache.Get(GetCacheKey(id), (owner: this, id), static value => value.owner.CreateOptions(value.id, null), cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<HttpClient> Get(string id, Func<CancellationToken, ValueTask<HttpClientOptions?>> optionsFactory,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return _httpClientCache.Get(GetCacheKey(id), (owner: this, id, optionsFactory),
            static async (value, token) => value.owner.CreateOptions(value.id, await value.optionsFactory(token)), cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<HttpClient> Get(string id, Func<HttpClientOptions?> optionsFactory,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return _httpClientCache.Get(GetCacheKey(id), (owner: this, id, optionsFactory),
            static value => value.owner.CreateOptions(value.id, value.optionsFactory()), cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<HttpClient> Get(string id, Func<ValueTask<HttpClientOptions?>> optionsFactory,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return _httpClientCache.Get(GetCacheKey(id), (owner: this, id, optionsFactory),
            static async (value, _) => value.owner.CreateOptions(value.id, await value.optionsFactory()), cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<HttpClient> Get<TState>(string id, TState state, Func<TState, HttpClientOptions?> optionsFactory,
        CancellationToken cancellationToken = default) where TState : notnull
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return _httpClientCache.Get(GetCacheKey(id), (owner: this, id, state, optionsFactory),
            static value => value.owner.CreateOptions(value.id, value.optionsFactory(value.state)), cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<HttpClient> Get<TState>(string id, TState state,
        Func<TState, CancellationToken, ValueTask<HttpClientOptions?>> optionsFactory,
        CancellationToken cancellationToken = default) where TState : notnull
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return _httpClientCache.Get(GetCacheKey(id), (owner: this, id, state, optionsFactory),
            static async (value, token) =>
                value.owner.CreateOptions(value.id, await value.optionsFactory(value.state, token)), cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HttpClient GetSync(string id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _httpClientCache.GetSync(GetCacheKey(id), () => CreateOptions(id, null), cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HttpClient GetSync(string id, Func<CancellationToken, ValueTask<HttpClientOptions?>> optionsFactory,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return _httpClientCache.GetSync(GetCacheKey(id), async token => CreateOptions(id, await optionsFactory(token)),
            cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HttpClient GetSync(string id, Func<HttpClientOptions?> optionsFactory,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return _httpClientCache.GetSync(GetCacheKey(id), () => CreateOptions(id, optionsFactory()), cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HttpClient GetSync(string id, Func<ValueTask<HttpClientOptions?>> optionsFactory,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return _httpClientCache.GetSync(GetCacheKey(id), async () => CreateOptions(id, await optionsFactory()),
            cancellationToken);
    }

    public async ValueTask Remove(string id)
    {
        ThrowIfDisposed();
        string cacheKey = GetCacheKey(id);

        try
        {
            await _httpClientCache.Remove(cacheKey);
        }
        finally
        {
            RemoveClientTracking(id);
        }
    }

    public void RemoveSync(string id)
    {
        ThrowIfDisposed();
        string cacheKey = GetCacheKey(id);

        try
        {
            _httpClientCache.RemoveSync(cacheKey);
        }
        finally
        {
            RemoveClientTracking(id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed.TrySetTrue())
            return;

        foreach (string id in _clientIds.Keys)
        {
            try
            {
                await _httpClientCache.Remove(_keyPrefix + id);
            }
            finally
            {
                RemoveClientTracking(id);
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed.TrySetTrue())
            return;

        foreach (string id in _clientIds.Keys)
        {
            try
            {
                _httpClientCache.RemoveSync(_keyPrefix + id);
            }
            finally
            {
                RemoveClientTracking(id);
            }
        }
    }

    private HttpClientOptions CreateOptions(string id, HttpClientOptions? options)
    {
        if (options?.ModifyPrimaryHandler is not null)
            throw new NotSupportedException(
                "Custom primary-handler configuration cannot be used by the SSRF-safe cache.");

        if (options?.Proxy is not null || options?.UseProxy == true)
            throw new NotSupportedException(
                "Proxies cannot be used by the SSRF-safe cache because the destination connection cannot be validated.");

        if (options?.SslOptions is not null)
            throw new NotSupportedException("Custom SSL options cannot be used by the SSRF-safe cache.");

        if (!_clientIds.TryAdd(id, 0))
            throw new InvalidOperationException($"An HTTP client is already registered for cache key '{id}'.");

        return new HttpClientOptions
        {
            Timeout = options?.Timeout,
            BaseAddress = options?.BaseAddress,
            DefaultRequestHeaders = options?.DefaultRequestHeaders,
            ModifyClient = options?.ModifyClient,
            DelegatingHandlerFactories = options?.DelegatingHandlerFactories,
            PooledConnectionLifetime = options?.PooledConnectionLifetime,
            UseCookieContainer = options?.UseCookieContainer,
            MaxConnectionsPerServer = options?.MaxConnectionsPerServer,
            ConnectTimeout = options?.ConnectTimeout,
            ResponseDrainTimeout = options?.ResponseDrainTimeout,
            AllowAutoRedirect = options?.AllowAutoRedirect,
            AutomaticDecompression = options?.AutomaticDecompression,
            KeepAlivePingDelay = options?.KeepAlivePingDelay,
            KeepAlivePingTimeout = options?.KeepAlivePingTimeout,
            KeepAlivePingPolicy = options?.KeepAlivePingPolicy,
            MaxResponseDrainSize = options?.MaxResponseDrainSize,
            MaxResponseHeadersLength = options?.MaxResponseHeadersLength,
            UseProxy = false,
            ModifyPrimaryHandler = ConfigurePrimaryHandler
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetCacheKey(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return _cacheKeys.GetOrAdd(id, static (key, prefix) => prefix + key, _keyPrefix);
    }

    private void RemoveClientTracking(string id)
    {
        _clientIds.TryRemove(id, out _);
        _cacheKeys.TryRemove(id, out _);
    }

    private void ConfigurePrimaryHandler(SocketsHttpHandler handler)
    {
        handler.UseProxy = false;
        handler.ConnectCallback = Connect;
    }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed.Read(), this);
}
