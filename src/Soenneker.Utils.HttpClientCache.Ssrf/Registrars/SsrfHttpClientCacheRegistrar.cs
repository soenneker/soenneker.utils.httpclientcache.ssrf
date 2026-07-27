using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Utils.HttpClientCache.Ssrf.Abstract;
using Soenneker.Utils.HttpClientCache.Registrar;
using Soenneker.Validators.IpAddresses.Ssrf.Registrars;

namespace Soenneker.Utils.HttpClientCache.Ssrf.Registrars;

/// <summary>
/// SSRF-safe, DNS-rebinding-resistant HttpClient caching.
/// </summary>
public static class SsrfHttpClientCacheRegistrar
{
    /// <summary>
    /// Adds <see cref="ISsrfHttpClientCache"/> as a singleton service. <para/>
    /// </summary>
    public static IServiceCollection AddSsrfHttpClientCacheAsSingleton(this IServiceCollection services)
    {
        services.AddHttpClientCacheAsSingleton().AddSsrfIpAddressValidatorAsSingleton()
                .TryAddSingleton<ISsrfHttpClientCache, SsrfHttpClientCache>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="ISsrfHttpClientCache"/> as a scoped service. <para/>
    /// </summary>
    public static IServiceCollection AddSsrfHttpClientCacheAsScoped(this IServiceCollection services)
    {
        services.AddHttpClientCacheAsScoped().AddSsrfIpAddressValidatorAsScoped()
                .TryAddScoped<ISsrfHttpClientCache, SsrfHttpClientCache>();

        return services;
    }
}