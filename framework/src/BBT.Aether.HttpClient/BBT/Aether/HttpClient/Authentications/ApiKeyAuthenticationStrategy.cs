using System.Threading.Tasks;

namespace BBT.Aether.HttpClient.Authentications;

internal sealed class ApiKeyAuthenticationStrategy(string headerKey, string apiKey) : IAuthenticationStrategy
{
    internal const string Type = "ApiKey";
    public Task AddAuthenticationAsync(System.Net.Http.HttpClient httpClient)
    {
        httpClient.DefaultRequestHeaders.Add(headerKey, apiKey);
        return Task.CompletedTask;
    }
}