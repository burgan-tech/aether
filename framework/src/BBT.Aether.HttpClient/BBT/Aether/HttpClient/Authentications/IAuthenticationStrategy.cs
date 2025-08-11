using System.Net.Http;
using System.Threading.Tasks;

namespace BBT.Aether.HttpClient.Authentications;

public interface IAuthenticationStrategy
{
    Task AddAuthenticationAsync(System.Net.Http.HttpClient httpClient);
}