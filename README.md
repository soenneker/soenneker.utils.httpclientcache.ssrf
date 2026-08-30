[![](https://img.shields.io/nuget/v/soenneker.utils.httpclientcache.ssrf.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.utils.httpclientcache.ssrf/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.utils.httpclientcache.ssrf/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.utils.httpclientcache.ssrf/actions/workflows/publish-package.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.utils.httpclientcache.ssrf/codeql.yml?style=for-the-badge&label=CodeQL)](https://github.com/soenneker/soenneker.utils.httpclientcache.ssrf/actions/workflows/codeql.yml)
[![](https://img.shields.io/nuget/dt/soenneker.utils.httpclientcache.ssrf.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.utils.httpclientcache.ssrf/)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.Utils.HttpClientCache.Ssrf
### SSRF-safe, DNS-rebinding-resistant HttpClient caching.

## Installation

```bash
dotnet add package Soenneker.Utils.HttpClientCache.Ssrf
```

## Registration

```csharp
using Soenneker.Utils.HttpClientCache.Ssrf.Registrars;

services.AddSsrfHttpClientCacheAsSingleton();
```

`AddSsrfHttpClientCacheAsScoped()` is also available. Both methods register the underlying
`Soenneker.Utils.HttpClientCache` service with the matching lifetime when it has not already
been registered.

## Usage

```csharp
using Soenneker.Utils.HttpClientCache.Ssrf.Abstract;

public sealed class RemoteDocumentClient
{
    private readonly ISsrfHttpClientCache _clientCache;

    public RemoteDocumentClient(ISsrfHttpClientCache clientCache)
    {
        _clientCache = clientCache;
    }

    public async ValueTask<string> Download(Uri uri, CancellationToken cancellationToken)
    {
        HttpClient client = await _clientCache.Get("remote-documents", cancellationToken);
        return await client.GetStringAsync(uri, cancellationToken);
    }
}
```

The cache implements the same API as `IHttpClientCache`, including synchronous and asynchronous
option factories, cache removal, and disposal. An `id` identifies one cached client within this
cache instance. Configure a given ID consistently: as with the underlying cache, the options
factory is used only when that client is first created.

## Security behavior

- DNS is resolved when a connection is opened.
- Every resolved address must be publicly routable.
- The socket connects directly to the validated address set, preventing a second DNS lookup from
  changing the destination.
- Redirect destinations pass through the same connection validation.
- Loopback, private, link-local, carrier-grade NAT, documentation, benchmark, multicast, and
  reserved address ranges are blocked for IPv4 and IPv6.
- Proxies, custom `HttpClientHandler` instances, and custom `SslOptions` are rejected because they
  expand or bypass the cache's controlled transport configuration.

Validation happens when a connection is opened, not when `Get` returns a client. A hostname that
resolves to any blocked address is rejected rather than falling back to another address. This
protects outbound connections; it does not impose an application-level allowlist for hosts or
paths.

The caller owns neither the returned `HttpClient` nor its handler. Use `Remove`/`RemoveSync` when a
cached client is no longer needed, or dispose the cache with its dependency-injection scope.
