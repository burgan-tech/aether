using System;
using System.Diagnostics;
using System.Linq;
using BBT.Aether.ExceptionHandling;
using BBT.Aether.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BBT.Aether.AspNetCore.ExceptionHandling;

/// <summary>
/// Default implementation of IProblemDetailsFactory that creates standardized ProblemDetails responses.
/// </summary>
public class ProblemDetailsFactory(
    IHttpExceptionStatusCodeFinder statusCodeFinder,
    IOptions<AetherExceptionHandlingOptions> options)
    : IProblemDetailsFactory
{
    public ProblemDetails CreateProblemDetails(Error error, HttpContext httpContext)
    {
        var statusCode = statusCodeFinder.GetStatusCodeFromError(error);
        
        // Use ValidationProblemDetails for validation errors
        if (error.Prefix.Equals(ErrorCodes.Prefixes.Validation, StringComparison.OrdinalIgnoreCase))
        {
            return CreateValidationProblemDetails(error, statusCode, httpContext);
        }

        return CreateStandardProblemDetails(error, statusCode, httpContext);
    }

    private ProblemDetails CreateStandardProblemDetails(
        Error error, 
        System.Net.HttpStatusCode statusCode, 
        HttpContext httpContext)
    {
        var problemDetails = new ProblemDetails
        {
            Status = (int)statusCode,
            Type = $"{options.Value.ErrorTypeBaseUrl.TrimEnd('/')}/{(int)statusCode}/{error.Prefix}/{error.Code.Replace(".", "/").Replace(":", "/")}",
            Title = GetTitleForStatusCode(statusCode),
            Detail = error.Message,
            Instance = httpContext.Request.Path
        };

        AddCommonExtensions(problemDetails, error);

        return problemDetails;
    }

    private ValidationProblemDetails CreateValidationProblemDetails(
        Error error, 
        System.Net.HttpStatusCode statusCode, 
        HttpContext httpContext)
    {
        var validationProblemDetails = new ValidationProblemDetails
        {
            Status = (int)statusCode,
            Type = $"{options.Value.ErrorTypeBaseUrl.TrimEnd('/')}/{(int)statusCode}/{error.Prefix}/{error.Code.Replace(".", "/").Replace(":", "/")}",
            Title = GetTitleForStatusCode(statusCode),
            Detail = error.Message,
            Instance = httpContext.Request.Path
        };

        // Populate the standard RFC 7807 errors dictionary
        if (error.ValidationErrors != null && error.ValidationErrors.Any())
        {
            foreach (var validationError in error.ValidationErrors)
            {
                var errorsByMember = error.ValidationErrors
                .SelectMany(ve => (ve.MemberNames?.Any() == true ? ve.MemberNames : new[] { "general" })
                    .Select(mn => new { MemberName = mn, ve.ErrorMessage }))
                .GroupBy(x => x.MemberName, x => x.ErrorMessage ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.ToArray());

                foreach (var kvp in errorsByMember)
                {
                    if (!validationProblemDetails.Errors.ContainsKey(kvp.Key))
                    {
                        validationProblemDetails.Errors.Add(kvp.Key, kvp.Value);
                    }
                }
            }
        }

        AddCommonExtensions(validationProblemDetails, error);

        return validationProblemDetails;
    }

    private void AddCommonExtensions(ProblemDetails problemDetails, Error error)
    {
        // Add custom extensions for Aether-specific error information
        problemDetails.Extensions["errorCode"] = $"{error.Prefix}.{error.Code}";
        problemDetails.Extensions["prefix"] = error.Prefix;
        problemDetails.Extensions["code"] = error.Code;
        problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? string.Empty;

        if (!string.IsNullOrEmpty(error.Detail))
        {
            problemDetails.Extensions["details"] = error.Detail;
        }

        if (!string.IsNullOrEmpty(error.Target))
        {
            problemDetails.Extensions["target"] = error.Target;
        }
    }

    private static string GetTitleForStatusCode(System.Net.HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => "Bad Request",
            System.Net.HttpStatusCode.Unauthorized => "Unauthorized",
            System.Net.HttpStatusCode.Forbidden => "Forbidden",
            System.Net.HttpStatusCode.NotFound => "Not Found",
            System.Net.HttpStatusCode.Conflict => "Conflict",
            System.Net.HttpStatusCode.InternalServerError => "Internal Server Error",
            System.Net.HttpStatusCode.BadGateway => "Bad Gateway",
            System.Net.HttpStatusCode.ServiceUnavailable => "Service Unavailable",
            System.Net.HttpStatusCode.NotImplemented => "Not Implemented",
            _ => "An error occurred"
        };
    }
}

