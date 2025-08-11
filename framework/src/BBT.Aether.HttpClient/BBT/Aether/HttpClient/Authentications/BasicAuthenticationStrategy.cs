using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace BBT.Aether.HttpClient.Authentications;

internal sealed class BasicAuthenticationStrategy(string apiKey) : IAuthenticationStrategy
{
    internal const string Type = "Basic";

    public Task AddAuthenticationAsync(System.Net.Http.HttpClient httpClient)
    {
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", apiKey);
        return Task.CompletedTask;
    }
}