using System;
using System.Net;
using BBT.Aether.Results;
using Microsoft.AspNetCore.Http;

namespace BBT.Aether.AspNetCore.ExceptionHandling;

public interface IHttpExceptionStatusCodeFinder
{
    HttpStatusCode GetStatusCode(HttpContext httpContext, Exception exception);
    
    /// <summary>
    /// Gets HTTP status code from Error object using Prefix and Code mapping.
    /// </summary>
    /// <param name="error">The error object</param>
    /// <returns>HTTP status code</returns>
    HttpStatusCode GetStatusCodeFromError(Error error);
}