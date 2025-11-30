using BBT.Aether.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Aether.AspNetCore.ExceptionHandling;

/// <summary>
/// Factory for creating ProblemDetails and ValidationProblemDetails from Error objects.
/// Provides a standardized way to convert errors to HTTP problem responses.
/// </summary>
public interface IProblemDetailsFactory
{
    /// <summary>
    /// Creates a ProblemDetails response from an Error.
    /// For validation errors (prefix = "validation"), returns ValidationProblemDetails.
    /// For all other errors, returns standard ProblemDetails.
    /// </summary>
    /// <param name="error">The error to convert</param>
    /// <param name="httpContext">The HTTP context</param>
    /// <returns>ProblemDetails or ValidationProblemDetails depending on error type</returns>
    ProblemDetails CreateProblemDetails(Error error, HttpContext httpContext);
}

