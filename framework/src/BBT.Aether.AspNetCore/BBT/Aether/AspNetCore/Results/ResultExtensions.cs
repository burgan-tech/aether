using System;
using System.Net;
using BBT.Aether.AspNetCore.ExceptionHandling;
using BBT.Aether.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.AspNetCore.Results;

/// <summary>
/// Extension methods for converting Result and Result&lt;T&gt; to ASP.NET Core ActionResult.
/// Leverages existing error code to HTTP status code mapping and ProblemDetails infrastructure.
/// </summary>
public static class ResultExtensions
{
    /// <summary>
    /// Converts a Result to an ActionResult.
    /// Success returns 200 OK, failure returns ProblemDetails with appropriate status code.
    /// </summary>
    /// <param name="result">The result to convert</param>
    /// <param name="httpContext">The HTTP context</param>
    /// <returns>ActionResult representing the operation result</returns>
    public static IActionResult ToActionResult(this Result result, HttpContext httpContext)
    {
        if (result.IsSuccess)
        {
            return new OkResult();
        }

        return CreateProblemResult(result.Error, httpContext);
    }

    /// <summary>
    /// Converts a Result&lt;T&gt; to an ActionResult.
    /// Success returns the value with 200 OK (or 204 NoContent if value is null).
    /// Failure returns ProblemDetails with appropriate status code.
    /// </summary>
    /// <typeparam name="T">The type of the value</typeparam>
    /// <param name="result">The result to convert</param>
    /// <param name="httpContext">The HTTP context</param>
    /// <param name="statusCodeSelector">Optional function to customize the success status code based on the value</param>
    /// <returns>ActionResult representing the operation result</returns>
    public static IActionResult ToActionResult<T>(
        this Result<T> result, 
        HttpContext httpContext, 
        Func<T, int>? statusCodeSelector = null)
    {
        if (result.IsSuccess)
        {
            // Auto-detect: if value is null, return NoContent
            if (result.Value == null)
            {
                return new NoContentResult();
            }

            // Allow customization of status code
            if (statusCodeSelector != null)
            {
                var statusCode = statusCodeSelector(result.Value);
                return new ObjectResult(result.Value)
                {
                    StatusCode = statusCode
                };
            }

            return new OkObjectResult(result.Value);
        }

        return CreateProblemResult(result.Error, httpContext);
    }

    /// <summary>
    /// Converts a Result&lt;T&gt; to a CreatedResult (201 Created).
    /// Success returns the value with 201 Created status.
    /// Failure returns ProblemDetails with appropriate status code.
    /// </summary>
    /// <typeparam name="T">The type of the value</typeparam>
    /// <param name="result">The result to convert</param>
    /// <param name="httpContext">The HTTP context</param>
    /// <param name="location">Optional location URI for the created resource</param>
    /// <returns>ActionResult representing the operation result</returns>
    public static IActionResult ToCreatedResult<T>(
        this Result<T> result, 
        HttpContext httpContext, 
        string? location = null)
    {
        if (result.IsSuccess)
        {
            if (string.IsNullOrEmpty(location))
            {
                return new ObjectResult(result.Value)
                {
                    StatusCode = (int)HttpStatusCode.Created
                };
            }

            return new CreatedResult(location, result.Value);
        }

        return CreateProblemResult(result.Error, httpContext);
    }

    /// <summary>
    /// Converts a Result&lt;T&gt; to an AcceptedResult (202 Accepted).
    /// Success returns the value with 202 Accepted status.
    /// Failure returns ProblemDetails with appropriate status code.
    /// </summary>
    /// <typeparam name="T">The type of the value</typeparam>
    /// <param name="result">The result to convert</param>
    /// <param name="httpContext">The HTTP context</param>
    /// <param name="location">Optional location URI to check the status of the request</param>
    /// <returns>ActionResult representing the operation result</returns>
    public static IActionResult ToAcceptedResult<T>(
        this Result<T> result, 
        HttpContext httpContext, 
        string? location = null)
    {
        if (result.IsSuccess)
        {
            if (string.IsNullOrEmpty(location))
            {
                return new ObjectResult(result.Value)
                {
                    StatusCode = (int)HttpStatusCode.Accepted
                };
            }

            return new AcceptedResult(location, result.Value);
        }

        return CreateProblemResult(result.Error, httpContext);
    }

    private static IActionResult CreateProblemResult(Error error, HttpContext httpContext)
    {
        var factory = httpContext.RequestServices.GetRequiredService<IProblemDetailsFactory>();
        var problemDetails = factory.CreateProblemDetails(error, httpContext);
        
        return new ObjectResult(problemDetails)
        {
            StatusCode = problemDetails.Status
        };
    }
}

