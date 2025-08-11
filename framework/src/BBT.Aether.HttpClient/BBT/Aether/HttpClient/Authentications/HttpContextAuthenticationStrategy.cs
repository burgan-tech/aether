using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace BBT.Aether.HttpClient.Authentications;

public class HttpContextAuthenticationStrategy(IHttpContextAccessor httpContextAccessor) : IAuthenticationStrategy
{
    internal const string Type = "HttpContext";

    public Task AddAuthenticationAsync(System.Net.Http.HttpClient httpClient)
    {
        var authorizationHeaderValue = httpContextAccessor.HttpContext!.Request.Headers
            .FirstOrDefault(h => h.Key == "Authorization").Value.ToString();
        if (!string.IsNullOrEmpty(authorizationHeaderValue))
        {
            if (!httpClient.DefaultRequestHeaders.Contains("Authorization"))
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", authorizationHeaderValue);
            }
        }

        return Task.CompletedTask;
    }
}