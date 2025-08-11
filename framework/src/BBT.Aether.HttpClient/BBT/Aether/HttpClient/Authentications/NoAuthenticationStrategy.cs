using System.Threading.Tasks;

namespace BBT.Aether.HttpClient.Authentications;

internal sealed class NoAuthenticationStrategy : IAuthenticationStrategy
{
    public Task AddAuthenticationAsync(System.Net.Http.HttpClient httpClient)
    {
        return Task.CompletedTask;
    }
}