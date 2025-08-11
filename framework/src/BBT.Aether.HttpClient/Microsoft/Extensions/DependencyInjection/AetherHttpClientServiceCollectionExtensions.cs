using System;
using System.Net.Http.Headers;
using BBT.Aether.HttpClient;
using BBT.Aether.HttpClient.Authentications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class AetherHttpClientServiceCollectionExtensions
{
    public static IServiceCollection RegisterHttpClient<TClient, THttpClientImplementation>(
        this IServiceCollection services)
        where TClient : class, IHttpClientWrapper
        where THttpClientImplementation : class, IHttpClientWrapper, TClient
    {
        var typeConcrete = typeof(THttpClientImplementation);
        var apiName = typeConcrete.Name.Replace("HttpClient", string.Empty).Replace("Client", string.Empty);

        services.AddHttpClient(typeConcrete.FullName!, (serviceProvider, c) =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var options = configuration
                .GetSection(HttpClientWrapper.ApiEndpointSection)
                .GetSection(apiName)
                .Get<ApiEndPointOptions>();

            c.BaseAddress = new Uri(options!.BaseUrl);

            c.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue(options.DefaultMediaTypeWithQualityHeaderValue));

            c.Timeout = TimeSpan.FromSeconds(Convert.ToInt32(options!.DefaultTimeOut));

            var defaultRequestHeaders = options!.DefaultRequestHeaders;

            foreach (var header in defaultRequestHeaders)
            {
                c.DefaultRequestHeaders.Add(header.Key, header.Value);
            }
        });

        services.AddScoped<TClient, THttpClientImplementation>();

        services.TryAddSingleton<IAuthenticationStrategyFactory, AuthenticationStrategyFactory>();
        services.TryAddScoped<ITokenService, TokenService>();

        return services;
    }
}