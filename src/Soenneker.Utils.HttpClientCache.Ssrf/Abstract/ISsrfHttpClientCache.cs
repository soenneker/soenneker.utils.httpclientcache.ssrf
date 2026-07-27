using Soenneker.Utils.HttpClientCache.Abstract;

namespace Soenneker.Utils.HttpClientCache.Ssrf.Abstract;

/// <summary>
/// Provides cached <see cref="System.Net.Http.HttpClient"/> instances whose connections are restricted to publicly routable addresses.
/// </summary>
/// <remarks>
/// DNS is resolved and validated when each connection is opened, and the connection is made directly to the validated addresses.
/// This prevents DNS rebinding from redirecting a request to a private or reserved network.
/// </remarks>
public interface ISsrfHttpClientCache : IHttpClientCache
{
}
