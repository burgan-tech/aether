using System;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.HttpClient.Authentications;

public interface IAuthenticationStrategyFactory
{
    IAuthenticationStrategy CreateStrategy(ApiEndPointAuthenticationOptions? options = null);
}

internal sealed class AuthenticationStrategyFactory(IServiceProvider serviceProvider, IConfiguration configuration)
    : IAuthenticationStrategyFactory
{
    public IAuthenticationStrategy CreateStrategy(ApiEndPointAuthenticationOptions? options = null)
    {
        if (options == null)
        {
            return new NoAuthenticationStrategy();
        }

        return options.Type switch
        {
            ApiKeyAuthenticationStrategy.Type => new ApiKeyAuthenticationStrategy(
                options!.Data["Header"], options!.Data["ApiKey"]),
            BasicAuthenticationStrategy.Type => new BasicAuthenticationStrategy(options!.Data["ApiKey"]),
            OAuthAuthenticationStrategy.Type => new OAuthAuthenticationStrategy(
                JsonSerializer.Deserialize<OAuthClientOptions>(options.Data["OAuth"])!,
                serviceProvider.GetRequiredService<ITokenService>()),
            HttpContextAuthenticationStrategy.Type => new HttpContextAuthenticationStrategy(
                serviceProvider.GetRequiredService<IHttpContextAccessor>()),
            _ => new NoAuthenticationStrategy()
        };
    }
}