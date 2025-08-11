using System;
using System.Net;
using BBT.Aether.Domain.Entities;
using BBT.Aether.ExceptionHandling;
using BBT.Aether.Validation;
using Microsoft.AspNetCore.Http;
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

        if (exception is IHasErrorCode exceptionWithErrorCode &&
            !exceptionWithErrorCode.Code.IsNullOrWhiteSpace())
        {
            if (Options.ErrorCodeToHttpStatusCodeMappings.TryGetValue(exceptionWithErrorCode.Code!, out var status))
            {
                return status;
            }
        }

        if (exception is AetherValidationException)
        {
            return HttpStatusCode.BadRequest;
        }

        if (exception is EntityNotFoundException)
        {
            return HttpStatusCode.NotFound;
        }

        if (exception is AetherDbConcurrencyException)
        {
            return HttpStatusCode.Conflict;
        }

        if (exception is NotImplementedException)
        {
            return HttpStatusCode.NotImplemented;
        }

        if (exception is IBusinessException)
        {
            return HttpStatusCode.Forbidden;
        }

        return HttpStatusCode.InternalServerError;
    }
}