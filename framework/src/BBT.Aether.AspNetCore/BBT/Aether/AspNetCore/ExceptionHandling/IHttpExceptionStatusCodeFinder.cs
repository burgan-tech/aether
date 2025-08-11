using System;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace BBT.Aether.AspNetCore.ExceptionHandling;

public interface IHttpExceptionStatusCodeFinder
{
    HttpStatusCode GetStatusCode(HttpContext httpContext, Exception exception);
}