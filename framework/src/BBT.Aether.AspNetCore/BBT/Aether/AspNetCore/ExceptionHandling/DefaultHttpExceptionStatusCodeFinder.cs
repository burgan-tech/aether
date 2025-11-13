using System;
using System.Net;
using BBT.Aether.Domain.Entities;
using BBT.Aether.ExceptionHandling;
using BBT.Aether.Results;
using BBT.Aether.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BBT.Aether.AspNetCore.ExceptionHandling;

public class DefaultHttpExceptionStatusCodeFinder(IOptions<AetherExceptionHttpStatusCodeOptions> options)
    : IHttpExceptionStatusCodeFinder
{
    protected AetherExceptionHttpStatusCodeOptions Options { get; } = options.Value;

    public virtual HttpStatusCode GetStatusCode(HttpContext httpContext, Exception exception)
    {
        if (exception is IHasHttpStatusCode exceptionWithHttpStatusCode &&
            exceptionWithHttpStatusCode.HttpStatusCode > 0)
        {
            return (HttpStatusCode)exceptionWithHttpStatusCode.HttpStatusCode;
        }

        // Convert exception to Error to get Prefix and Code
        var exceptionToErrorConverter = httpContext.RequestServices.GetRequiredService<IExceptionToErrorInfoConverter>();
        var error = exceptionToErrorConverter.ConvertToError(exception);

        return GetStatusCodeFromError(error);
    }

    /// <summary>
    /// Gets HTTP status code from Error object using Prefix and Code mapping.
    /// </summary>
    /// <param name="error">The error object</param>
    /// <returns>HTTP status code</returns>
    public virtual HttpStatusCode GetStatusCodeFromError(Error error)
    {
        // First try to map by full code (prefix.code)
        var fullCode = $"{error.Prefix}.{error.Code}";
        if (Options.ErrorCodeToHttpStatusCodeMappings.TryGetValue(fullCode, out var statusCode))
        {
            return statusCode;
        }

        // Then try to map by just the code
        if (Options.ErrorCodeToHttpStatusCodeMappings.TryGetValue(error.Code, out statusCode))
        {
            return statusCode;
        }

        // Finally, try to map by prefix with default mapping
        return GetDefaultStatusCodeForPrefix(error.Prefix);
    }

    /// <summary>
    /// Gets default HTTP status code for a given prefix.
    /// </summary>
    /// <param name="prefix">The error prefix</param>
    /// <returns>Default HTTP status code for the prefix</returns>
    protected virtual HttpStatusCode GetDefaultStatusCodeForPrefix(string prefix)
    {
        return prefix switch
        {
            "validation" => HttpStatusCode.BadRequest,
            "conflict" => HttpStatusCode.Conflict,
            "notfound" => HttpStatusCode.NotFound,
            "unauthorized" => HttpStatusCode.Unauthorized,
            "forbidden" => HttpStatusCode.Forbidden,
            "dependency" => HttpStatusCode.BadGateway,
            "transient" => HttpStatusCode.ServiceUnavailable,   
            "failure" => HttpStatusCode.InternalServerError,
            _ => HttpStatusCode.InternalServerError
        };
    }
}