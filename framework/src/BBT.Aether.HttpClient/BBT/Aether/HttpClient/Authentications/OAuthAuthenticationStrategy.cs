using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace BBT.Aether.HttpClient.Authentications;

internal sealed class OAuthAuthenticationStrategy(OAuthClientOptions clientOptions, ITokenService tokenService)
    : IAuthenticationStrategy
{
    internal const string Type = "OAuth";

    public async Task AddAuthenticationAsync(System.Net.Http.HttpClient httpClient)
    {
        var token = await tokenService.GetAccessTokenAsync(clientOptions);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}