using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Aether.HttpClient;

public interface IHttpClientWrapper
{
    Task<HttpResponseMessage> DeleteAsync(string requestUri, CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> DeleteAsync(string requestUri, IDictionary<string, string>? headers,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> DeleteAsync(string requestUri, IDictionary<string, string>? headers, TimeSpan? timeout,
        CancellationToken cancellationToken = default);

    Task<TResult> DeleteAsync<TResult>(string requestUri, CancellationToken cancellationToken = default);

    Task<TResult> DeleteAsync<TResult>(string requestUri, IDictionary<string, string>? headers,
        CancellationToken cancellationToken = default);

    Task<TResult> DeleteAsync<TResult>(string requestUri, IDictionary<string, string>? headers, TimeSpan? timeout,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> GetAsync(string requestUri, CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> GetAsync(string requestUri, IDictionary<string, string>? headers,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> GetAsync(string requestUri, IDictionary<string, string>? headers, TimeSpan? timeout,
        CancellationToken cancellationToken = default);

    Task<TResult> GetAsync<TResult>(string requestUri, CancellationToken cancellationToken = default);

    Task<TResult> GetAsync<TResult>(string requestUri, IDictionary<string, string>? headers,
        CancellationToken cancellationToken = default);

    Task<TResult> GetAsync<TResult>(string requestUri, IDictionary<string, string>? headers, TimeSpan? timeout,
        CancellationToken cancellationToken = default);


    Task<HttpResponseMessage> PostAsync(string requestUri, HttpContent content,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> PostAsync(string requestUri, IDictionary<string, string>? headers, HttpContent content,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> PostAsync(string requestUri, IDictionary<string, string>? headers, HttpContent content,
        TimeSpan? timeout, CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> PostAsync<TContent>(string requestUri, TContent obj,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> PostAsync<TContent>(string requestUri, IDictionary<string, string>? headers, TContent obj,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> PostAsync<TContent>(string requestUri, IDictionary<string, string>? headers, TContent obj,
        TimeSpan? timeout, CancellationToken cancellationToken = default);

    Task<TResult> PostAsync<TContent, TResult>(string requestUri, TContent obj,
        CancellationToken cancellationToken = default);

    Task<TResult> PostAsync<TContent, TResult>(string requestUri, IDictionary<string, string>? headers, TContent obj,
        CancellationToken cancellationToken = default);

    Task<TResult> PostAsync<TContent, TResult>(string requestUri, IDictionary<string, string>? headers, TContent obj,
        TimeSpan? timeout, CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> PutAsync(string requestUri, HttpContent content,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> PutAsync(string requestUri, IDictionary<string, string>? headers, HttpContent content,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> PutAsync(string requestUri, IDictionary<string, string>? headers, HttpContent content,
        TimeSpan? timeout, CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> PutAsync<TContent>(string requestUri, TContent obj,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> PutAsync<TContent>(string requestUri, IDictionary<string, string>? headers, TContent obj,
        CancellationToken cancellationToken = default);

    Task<HttpResponseMessage> PutAsync<TContent>(string requestUri, IDictionary<string, string>? headers, TContent obj,
        TimeSpan? timeout, CancellationToken cancellationToken = default);

    Task<TResult> PutAsync<TContent, TResult>(string requestUri, TContent obj,
        CancellationToken cancellationToken = default);

    Task<TResult> PutAsync<TContent, TResult>(string requestUri, IDictionary<string, string>? headers, TContent obj,
        CancellationToken cancellationToken = default);

    Task<TResult> PutAsync<TContent, TResult>(string requestUri, IDictionary<string, string>? headers, TContent obj,
        TimeSpan? timeout, CancellationToken cancellationToken = default);
}