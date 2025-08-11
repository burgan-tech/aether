using System;
using System.Net;
using Microsoft.Extensions.Logging;

namespace BBT.Aether.HttpClient;

public class SimpleHttpResponseException(
    HttpStatusCode statusCode,
    string? callRequestUrl,
    string? callRequestPath,
    string? content,
    Exception? innerException = null)
    : Exception(string.Concat(statusCode, " received from ", callRequestUrl, "/", callRequestPath, ". Message is '",
        content,
        "'"), innerException)
{
    public HttpStatusCode StatusCode { get; private set; } = statusCode;
    public string? CallRequestUrl { get; private set; } = callRequestUrl;
    public string? CallRequestPath { get; private set; } = callRequestPath;
}