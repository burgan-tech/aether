using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.HttpClient.Authentications;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.HttpClient;

public abstract class HttpClientWrapper
    : IHttpClientWrapper
{
    protected abstract string ApiName { get; }
    internal const string ApiEndpointSection = "ApiEndPoints";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAuthenticationStrategy _authenticationStrategy;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ApiEndPointOptions? _apiEndPointOptions;
    private readonly ILogger<HttpClientWrapper> _logger;

    protected HttpClientWrapper(
        IHttpClientFactory httpClientFactory,
        IAuthenticationStrategyFactory authenticationStrategyFactory,
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration,
        ILogger<HttpClientWrapper> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;

        _apiEndPointOptions = configuration
            .GetSection(ApiEndpointSection)
            .GetSection(ApiName)
            .Get<ApiEndPointOptions>();

        _authenticationStrategy = authenticationStrategyFactory
            .CreateStrategy(_apiEndPointOptions?.Authentication);
    }

    public Task<HttpResponseMessage> DeleteAsync(string requestUri, CancellationToken cancellationToken = default)
    {
        return DeleteAsync(requestUri, null, cancellationToken);
    }

    public Task<HttpResponseMessage> DeleteAsync(string requestUri, IDictionary<string, string>? headers,
        CancellationToken cancellationToken = default)
    {
        return DeleteAsync(requestUri, headers, null, cancellationToken);
    }

    public Task<HttpResponseMessage> DeleteAsync(string requestUri, IDictionary<string, string>? headers,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default)
    {
        return HttpClientDeleteAsync(requestUri, headers, timeout, true, cancellationToken);
    }

    public Task<TResult> DeleteAsync<TResult>(string requestUri, CancellationToken cancellationToken = default)
    {
        return DeleteAsync<TResult>(requestUri, null, null, cancellationToken);
    }

    public Task<TResult> DeleteAsync<TResult>(string requestUri, IDictionary<string, string>? headers,
        CancellationToken cancellationToken = default)
    {
        return DeleteAsync<TResult>(requestUri, headers, null, cancellationToken);
    }

    public async Task<TResult> DeleteAsync<TResult>(string requestUri, IDictionary<string, string>? headers,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default)
    {
        var resp = await HttpClientDeleteAsync(requestUri, headers, timeout, false, cancellationToken);
        return (await DeserializeResponseToObject<TResult>(resp, cancellationToken))!;
    }

    public Task<HttpResponseMessage> GetAsync(string requestUri, CancellationToken cancellationToken = default)
    {
        return GetAsync(requestUri, null, cancellationToken);
    }

    public Task<HttpResponseMessage> GetAsync(string requestUri, IDictionary<string, string>? headers,
        CancellationToken cancellationToken = default)
    {
        return GetAsync(requestUri, headers, null, cancellationToken);
    }

    public Task<HttpResponseMessage> GetAsync(string requestUri, IDictionary<string, string>? headers,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default)
    {
        return HttpClientGetAsync(requestUri, headers, timeout, true, cancellationToken);
    }


    public Task<TResult> GetAsync<TResult>(string requestUri, CancellationToken cancellationToken = default)
    {
        return GetAsync<TResult>(requestUri, null, null, cancellationToken);
    }

    public Task<TResult> GetAsync<TResult>(string requestUri, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return GetAsync<TResult>(requestUri, null, timeout, cancellationToken);
    }

    public Task<TResult> GetAsync<TResult>(string requestUri, IDictionary<string, string>? headers,
        CancellationToken cancellationToken = default)
    {
        return GetAsync<TResult>(requestUri, headers, null, cancellationToken);
    }

    public async Task<TResult> GetAsync<TResult>(string requestUri, IDictionary<string, string>? headers,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default)
    {
        var resp = await HttpClientGetAsync(requestUri, headers, timeout, false, cancellationToken);
        return (await DeserializeResponseToObject<TResult?>(resp, cancellationToken))!;
    }


    public Task<HttpResponseMessage> PostAsync(string requestUri, HttpContent content,
        CancellationToken cancellationToken = default)
    {
        return PostAsync(requestUri, null, content, cancellationToken);
    }

    public Task<HttpResponseMessage> PostAsync(string requestUri, IDictionary<string, string>? headers,
        HttpContent content,
        CancellationToken cancellationToken = default)
    {
        return PostAsync(requestUri, headers, content, null, cancellationToken);
    }

    public Task<HttpResponseMessage> PostAsync(string requestUri, IDictionary<string, string>? headers,
        HttpContent content,
        TimeSpan? timeout, CancellationToken cancellationToken = default)
    {
        return HttpClientPostAsync(requestUri, headers, content, timeout, true, cancellationToken);
    }


    public Task<HttpResponseMessage> PostAsync<TContent>(string requestUri, TContent obj,
        CancellationToken cancellationToken = default)
    {
        return PostAsync(requestUri, null, obj, null, cancellationToken);
    }

    public Task<HttpResponseMessage> PostAsync<TContent>(string requestUri, IDictionary<string, string>? headers,
        TContent obj,
        CancellationToken cancellationToken = default)
    {
        return PostAsync(requestUri, headers, obj, null, cancellationToken);
    }

    public Task<HttpResponseMessage> PostAsync<TContent>(string requestUri, IDictionary<string, string>? headers,
        TContent obj,
        TimeSpan? timeout, CancellationToken cancellationToken = default)
    {
        return PostAsync(requestUri, headers,
            obj is HttpContent httpContent ? httpContent : SerializeObjectToContent(obj), timeout,
            cancellationToken);
    }

    public Task<TResult> PostAsync<TContent, TResult>(string requestUri, TContent obj,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<TContent, TResult>(requestUri, null, obj, null, cancellationToken);
    }

    public Task<TResult> PostAsync<TContent, TResult>(string requestUri, IDictionary<string, string>? headers,
        TContent obj,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<TContent, TResult>(requestUri, headers, obj, null, cancellationToken);
    }


    public async Task<TResult> PostAsync<TContent, TResult>(string requestUri, IDictionary<string, string>? headers,
        TContent obj,
        TimeSpan? timeout, CancellationToken cancellationToken = default)
    {
        var resp = await HttpClientPostAsync(requestUri, headers,
            obj is HttpContent httpContent ? httpContent : SerializeObjectToContent(obj), timeout, false,
            cancellationToken);
        return (await DeserializeResponseToObject<TResult>(resp, cancellationToken))!;
    }


    public Task<HttpResponseMessage> PutAsync(string requestUri, HttpContent content,
        CancellationToken cancellationToken = default)
    {
        return PutAsync(requestUri, null, content, cancellationToken);
    }

    public Task<HttpResponseMessage> PutAsync(string requestUri, IDictionary<string, string>? headers,
        HttpContent content,
        CancellationToken cancellationToken = default)
    {
        return PutAsync(requestUri, headers, content, null, cancellationToken);
    }

    public Task<HttpResponseMessage> PutAsync(string requestUri, IDictionary<string, string>? headers,
        HttpContent content,
        TimeSpan? timeout, CancellationToken cancellationToken = default)
    {
        return HttpClientPutAsync(requestUri, headers, content, timeout, true, cancellationToken);
    }


    public Task<HttpResponseMessage> PutAsync<TContent>(string requestUri, TContent obj,
        CancellationToken cancellationToken = default)
    {
        return PutAsync(requestUri, null, obj, null, cancellationToken);
    }

    public Task<HttpResponseMessage> PutAsync<TContent>(string requestUri, IDictionary<string, string>? headers,
        TContent obj,
        CancellationToken cancellationToken = default)
    {
        return PutAsync(requestUri, headers, obj, null, cancellationToken);
    }

    public Task<HttpResponseMessage> PutAsync<TContent>(string requestUri, IDictionary<string, string>? headers,
        TContent obj,
        TimeSpan? timeout, CancellationToken cancellationToken = default)
    {
        return PutAsync(requestUri, headers,
            obj is HttpContent httpContent ? httpContent : SerializeObjectToContent(obj), timeout,
            cancellationToken);
    }

    public Task<TResult> PutAsync<TContent, TResult>(string requestUri, TContent obj,
        CancellationToken cancellationToken = default)
    {
        return PutAsync<TContent, TResult>(requestUri, null, obj, null, cancellationToken);
    }

    public Task<TResult> PutAsync<TContent, TResult>(string requestUri, IDictionary<string, string>? headers,
        TContent obj,
        CancellationToken cancellationToken = default)
    {
        return PutAsync<TContent, TResult>(requestUri, headers, obj, null, cancellationToken);
    }

    public async Task<TResult> PutAsync<TContent, TResult>(string requestUri, IDictionary<string, string>? headers,
        TContent obj,
        TimeSpan? timeout, CancellationToken cancellationToken = default)
    {
        var resp = await HttpClientPutAsync(requestUri, headers,
            obj is HttpContent httpContent ? httpContent : SerializeObjectToContent(obj), timeout, false,
            cancellationToken);
        return (await DeserializeResponseToObject<TResult>(resp, cancellationToken))!;
    }


    #region private region

    private Task<HttpResponseMessage> HttpClientDeleteAsync(string requestUri, IDictionary<string, string>? headers,
        TimeSpan? timeout, bool isResponseHttpResponseMessage, CancellationToken cancellationToken = default)
    {
        var httpClient = CreateCoreHttpClient();
        return MakeApiRequestCore(httpClient, httpClient.DeleteAsync, requestUri, headers, timeout,
            isResponseHttpResponseMessage, cancellationToken);
    }

    private Task<HttpResponseMessage> HttpClientGetAsync(string requestUri, IDictionary<string, string>? headers,
        TimeSpan? timeout,
        bool isResponseHttpResponseMessage, CancellationToken cancellationToken = default)
    {
        var httpClient = CreateCoreHttpClient();
        return MakeApiRequestCore(httpClient, httpClient.GetAsync, requestUri, headers, timeout,
            isResponseHttpResponseMessage, cancellationToken);
    }

    private Task<HttpResponseMessage> HttpClientPostAsync(string requestUri, IDictionary<string, string>? headers,
        HttpContent content, TimeSpan? timeout, bool isResponseHttpResponseMessage,
        CancellationToken cancellationToken = default)
    {
        var httpClient = CreateCoreHttpClient();
        return MakeApiRequestCore(httpClient, httpClient.PostAsync, requestUri, content, headers,
            timeout, isResponseHttpResponseMessage, cancellationToken);
    }

    private Task<HttpResponseMessage> HttpClientPutAsync(string requestUri, IDictionary<string, string>? headers,
        HttpContent content,
        TimeSpan? timeout, bool isResponseHttpResponseMessage, CancellationToken cancellationToken = default)
    {
        var httpClient = CreateCoreHttpClient();
        return MakeApiRequestCore(httpClient, httpClient.PutAsync, requestUri, content, headers,
            timeout, isResponseHttpResponseMessage, cancellationToken);
    }

    private System.Net.Http.HttpClient CreateCoreHttpClient()
    {
        return _httpClientFactory.CreateClient(GetType().FullName!);
    }

    private async Task<HttpResponseMessage> MakeApiRequestCore(
        System.Net.Http.HttpClient httpClient, Func<string, CancellationToken, Task<HttpResponseMessage>> callMethod,
        string requestUri,
        IDictionary<string, string>? headers, TimeSpan? timeout, bool isResponseHttpResponseMessage,
        CancellationToken cancellationToken)
    {
        await OnBeforeApiRequest(httpClient, headers, timeout);
        HttpResponseMessage response;
        var stopWatch = new Stopwatch();
        stopWatch.Start();
        var statusCode = 0;
        try
        {
            response = await callMethod(requestUri, cancellationToken);

            statusCode = (int)response.StatusCode;
            if (isResponseHttpResponseMessage)
                return response;

            if (response.IsSuccessStatusCode)
                return response;

            var contentStr = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(EventIdConstants.HttpServiceCallerFailureEventId,
                "{Url} call failure {ElapsedMilliseconds} milliseconds with status {StatusCode} for {HttpMethod} {Content}.",
                $"{httpClient.BaseAddress?.ToString()}/{requestUri}",
                stopWatch.ElapsedMilliseconds,
                statusCode,
                $"{callMethod.Method.Name}", contentStr);

            throw new SimpleHttpResponseException(response.StatusCode,
                httpClient.BaseAddress?.ToString(),
                requestUri,
                contentStr);
        }
        finally
        {
            stopWatch.Stop();
            _logger.LogInformation(EventIdConstants.HttpServiceCallerEventId,
                "{Url} call lasted {ElapsedMilliseconds} milliseconds with status {StatusCode} for {HttpMethod}.",
                $"{httpClient.BaseAddress?.ToString()}/{requestUri}",
                stopWatch.ElapsedMilliseconds,
                statusCode,
                $"{callMethod.Method.Name}");
        }
    }

    private async Task<HttpResponseMessage> MakeApiRequestCore(System.Net.Http.HttpClient httpClient,
        Func<string, HttpContent, CancellationToken, Task<HttpResponseMessage>> callMethod, string requestUri,
        HttpContent content, IDictionary<string, string>? headers, TimeSpan? timeout,
        bool isResponseHttpResponseMessage,
        CancellationToken cancellationToken)
    {
        await OnBeforeApiRequest(httpClient, headers, timeout);
        var stopWatch = new Stopwatch();
        stopWatch.Start();
        var statusCode = 0;
        try
        {
            var response = await callMethod(requestUri, content, cancellationToken);
            statusCode = (int)response.StatusCode;
            if (isResponseHttpResponseMessage)
                return response;

            if (response.IsSuccessStatusCode)
                return response;

            var contentStr = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(EventIdConstants.HttpServiceCallerEventId,
                "{Url} call failure {ElapsedMilliseconds} milliseconds with status {StatusCode} for {HttpMethod} {Content}.",
                $"{httpClient.BaseAddress?.ToString()}/{requestUri}",
                stopWatch.ElapsedMilliseconds,
                statusCode,
                $"{callMethod.Method.Name}", contentStr);
            throw new SimpleHttpResponseException(response.StatusCode,
                httpClient.BaseAddress?.ToString(),
                requestUri,
                contentStr
            );
        }
        finally
        {
            stopWatch.Stop();
            _logger.LogInformation(EventIdConstants.HttpServiceCallerFailureEventId,
                "{Url} call lasted {ElapsedMilliseconds} milliseconds with status {StatusCode} for {HttpMethod}.",
                $"{httpClient.BaseAddress?.ToString()}/{requestUri}",
                stopWatch.ElapsedMilliseconds,
                statusCode,
                $"{callMethod.Method.Name}");
        }
    }

    protected virtual async Task OnBeforeApiRequest(
        System.Net.Http.HttpClient httpClient,
        IDictionary<string, string>? headersCall,
        TimeSpan? timeout)
    {
        if (timeout != null)
        {
            httpClient.Timeout = timeout.Value;
        }

        await _authenticationStrategy.AddAuthenticationAsync(httpClient);

        var headers = CollectHeaders();
        AddHeadersToHttpClient(httpClient, headers);

        if (headersCall != null)
        {
            AddHeadersToHttpClient(httpClient, headersCall.ToDictionary());
        }
    }

    private Dictionary<string, string> CollectHeaders()
    {
        var headers = new Dictionary<string, string>();
        return headers;
    }

    private void AddHeaderIfNotEmpty(Dictionary<string, string> headers, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            headers[key] = value;
        }
    }

    private void AddHeadersToHttpClient(System.Net.Http.HttpClient httpClient, Dictionary<string, string> headers)
    {
        foreach (var header in headers)
        {
            if (header.Key == "User-Agent")
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }
            else
            {
                httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
            }
        }
    }

    private async static Task<TResult?> DeserializeResponseToObject<TResult>(HttpResponseMessage resp,
        CancellationToken cancellationToken)
    {
        var responseBody =
            await new StreamReader(await resp.Content.ReadAsStreamAsync(cancellationToken)).ReadToEndAsync(
                cancellationToken);

        if (string.IsNullOrEmpty(responseBody) && (resp.RequestMessage?.Method == HttpMethod.Put ||
                                                   resp.RequestMessage?.Method == HttpMethod.Post))
        {
            return (TResult)Convert.ChangeType("", typeof(TResult));
        }

        if (!string.IsNullOrEmpty(responseBody))
        {
            return JsonSerializer.Deserialize<TResult?>(responseBody, DefaultJsonNamingPolicy());
        }

        if (resp.StatusCode == HttpStatusCode.NoContent)
        {
            return default(TResult);
        }

        throw new InvalidOperationException("Response body is empty");
    }

    private static JsonSerializerOptions DefaultJsonNamingPolicy()
    {
        return new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    }

    private static StringContent SerializeObjectToContent<TContent>(TContent obj)
    {
        return new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");
    }

    #endregion
}