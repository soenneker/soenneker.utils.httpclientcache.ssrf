using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Atomics.ValueBools;
using Soenneker.Dictionaries.SingletonKeys;
using Soenneker.Dtos.HttpClientOptions;
using Soenneker.Utils.HttpClientCache.Abstract;
using Soenneker.Utils.HttpClientCache.Ssrf.Abstract;
using Soenneker.Utils.HttpClientCache.Ssrf.Dtos;
using Soenneker.Validators.IpAddresses.Ssrf.Abstract;

namespace Soenneker.Utils.HttpClientCache.Ssrf;

/// <inheritdoc cref="ISsrfHttpClientCache"/>
public sealed class SsrfHttpClientCache : ISsrfHttpClientCache
{
    private readonly IHttpClientCache _httpClientCache;
    private readonly ISsrfIpAddressValidator _ipAddressValidator;
    private readonly SingletonKeyDictionary<HandlerKey, SsrfHttpClientHandler> _handlers;
    private readonly ConcurrentDictionary<string, byte> _clientIds = new(StringComparer.Ordinal);
    private readonly string _keyPrefix = $"{typeof(SsrfHttpClientCache).FullName}:{Guid.NewGuid():N}:";

    private ValueAtomicBool _disposed;

    public SsrfHttpClientCache(IHttpClientCache httpClientCache, ISsrfIpAddressValidator ipAddressValidator)
    {
        _httpClientCache = httpClientCache;
        _ipAddressValidator = ipAddressValidator;
        _handlers = new SingletonKeyDictionary<HandlerKey, SsrfHttpClientHandler>(CreateHandler);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<HttpClient> Get(string id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _httpClientCache.Get(GetCacheKey(id), () => CreateOptions(id, null), cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<HttpClient> Get(string id, Func<CancellationToken, ValueTask<HttpClientOptions?>> optionsFactory,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return _httpClientCache.Get(GetCacheKey(id), async token => CreateOptions(id, await optionsFactory(token)), cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<HttpClient> Get(string id, Func<HttpClientOptions?> optionsFactory, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return _httpClientCache.Get(GetCacheKey(id), () => CreateOptions(id, optionsFactory()), cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<HttpClient> Get(string id, Func<ValueTask<HttpClientOptions?>> optionsFactory, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return _httpClientCache.Get(GetCacheKey(id), async () => CreateOptions(id, await optionsFactory()), cancellationToken);
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
        Func<TState, CancellationToken, ValueTask<HttpClientOptions?>> optionsFactory, CancellationToken cancellationToken = default)
        where TState : notnull
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return _httpClientCache.Get(GetCacheKey(id), (owner: this, id, state, optionsFactory),
            static async (value, token) => value.owner.CreateOptions(value.id, await value.optionsFactory(value.state, token)), cancellationToken);
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

        return _httpClientCache.GetSync(GetCacheKey(id), async token => CreateOptions(id, await optionsFactory(token)), cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HttpClient GetSync(string id, Func<HttpClientOptions?> optionsFactory, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return _httpClientCache.GetSync(GetCacheKey(id), () => CreateOptions(id, optionsFactory()), cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HttpClient GetSync(string id, Func<ValueTask<HttpClientOptions?>> optionsFactory, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return _httpClientCache.GetSync(GetCacheKey(id), async () => CreateOptions(id, await optionsFactory()), cancellationToken);
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

        await _handlers.DisposeAsync();
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

        _handlers.Dispose();
    }

    private HttpClientOptions CreateOptions(string id, HttpClientOptions? options)
    {
        if (options?.HttpClientHandler is not null)
            throw new NotSupportedException("A custom HttpClientHandler cannot be used by the SSRF-safe cache.");

        if (options?.Proxy is not null || options?.UseProxy == true)
            throw new NotSupportedException("Proxies cannot be used by the SSRF-safe cache because the destination connection cannot be validated.");

        if (options?.SslOptions is not null)
            throw new NotSupportedException("Custom SSL options cannot be used by the SSRF-safe cache.");

        SsrfHttpClientHandler handler = _handlers.GetSync(CreateHandlerKey(options));

        if (!_clientIds.TryAdd(id, 0))
            throw new InvalidOperationException($"An HTTP client is already registered for cache key '{id}'.");

        return new HttpClientOptions
        {
            Timeout = options?.Timeout,
            BaseAddress = options?.BaseAddress,
            DefaultRequestHeaders = options?.DefaultRequestHeaders,
            ModifyClient = options?.ModifyClient,
            HttpClientHandler = handler
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetCacheKey(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return _keyPrefix + id;
    }

    private void RemoveClientTracking(string id)
    {
        _clientIds.TryRemove(id, out _);
    }

    private SsrfHttpClientHandler CreateHandler(HandlerKey key) =>
        new(_ipAddressValidator, new HttpClientOptions
        {
            PooledConnectionLifetime = TimeSpan.FromTicks(key.PooledConnectionLifetimeTicks),
            MaxConnectionsPerServer = key.MaxConnectionsPerServer,
            UseCookieContainer = key.UseCookies,
            ConnectTimeout = TimeSpan.FromTicks(key.ConnectTimeoutTicks),
            ResponseDrainTimeout = key.ResponseDrainTimeoutTicks.HasValue ? TimeSpan.FromTicks(key.ResponseDrainTimeoutTicks.Value) : null,
            AllowAutoRedirect = key.AllowAutoRedirect,
            AutomaticDecompression = key.AutomaticDecompression,
            KeepAlivePingDelay = key.KeepAlivePingDelayTicks.HasValue ? TimeSpan.FromTicks(key.KeepAlivePingDelayTicks.Value) : null,
            KeepAlivePingTimeout = key.KeepAlivePingTimeoutTicks.HasValue ? TimeSpan.FromTicks(key.KeepAlivePingTimeoutTicks.Value) : null,
            KeepAlivePingPolicy = key.KeepAlivePingPolicy,
            MaxResponseDrainSize = key.MaxResponseDrainSize,
            MaxResponseHeadersLength = key.MaxResponseHeadersLength
        });

    private static HandlerKey CreateHandlerKey(HttpClientOptions? options) =>
        new(PooledConnectionLifetimeTicks: (options?.PooledConnectionLifetime ?? TimeSpan.FromMinutes(10)).Ticks,
            MaxConnectionsPerServer: options?.MaxConnectionsPerServer ?? 40, UseCookies: options?.UseCookieContainer == true,
            ConnectTimeoutTicks: (options?.ConnectTimeout ?? TimeSpan.FromSeconds(100)).Ticks,
            ResponseDrainTimeoutTicks: options?.ResponseDrainTimeout?.Ticks, AllowAutoRedirect: options?.AllowAutoRedirect,
            AutomaticDecompression: options?.AutomaticDecompression, KeepAlivePingDelayTicks: options?.KeepAlivePingDelay?.Ticks,
            KeepAlivePingTimeoutTicks: options?.KeepAlivePingTimeout?.Ticks, KeepAlivePingPolicy: options?.KeepAlivePingPolicy,
            MaxResponseDrainSize: options?.MaxResponseDrainSize, MaxResponseHeadersLength: options?.MaxResponseHeadersLength);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed.Read(), this);
}
