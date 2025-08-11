using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace BBT.Aether.HttpClient.Authentications;

internal interface ITokenService
{
    Task<string?> GetAccessTokenAsync(OAuthClientOptions clientOptions);
}

internal sealed class TokenService(System.Net.Http.HttpClient httpClient)
    : ITokenService
{
    public async Task<string?> GetAccessTokenAsync(OAuthClientOptions clientOptions)
    {
        var response = await httpClient.PostAsync(clientOptions.TokenUrl,
            new FormUrlEncodedContent([
                new KeyValuePair<string, string?>("client_id", clientOptions.ClientId),
                new KeyValuePair<string, string?>("client_secret", clientOptions.ClientSecret),
                new KeyValuePair<string, string?>("grant_type", "client_credentials"),
                new KeyValuePair<string, string?>("scopes", clientOptions.Scopes)
            ]));

        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var tokenResponse = JsonSerializer.Deserialize<OAuthTokenResponse?>(content,
            new JsonSerializerOptions());
        return tokenResponse?.AccessToken;
    }
}

internal sealed class OAuthTokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
}

internal sealed class OAuthClientOptions
{
    public string TokenUrl { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string? Scopes { get; set; } = "";
}