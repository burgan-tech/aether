using System.Collections.Generic;
using BBT.Aether.Validation;

namespace BBT.Aether.Results;

/// <summary>
/// Represents an error with structured information.
/// Provides factory methods for common error types following a consistent naming convention.
/// </summary>
/// <param name="Prefix">The error prefix, typically indicating the category (validation, conflict, etc.)</param>
/// <param name="Code">The error code, typically in format "category.specific"</param>
/// <param name="Message">Human-readable error message</param>
/// <param name="Detail">Additional details about the error</param>
/// <param name="Target">The target field or resource that caused the error</param>
/// <param name="ValidationErrors">Collection of validation errors for detailed field-level error reporting</param>
public readonly record struct Error(
    string Prefix,
    string Code, 
    string? Message = null, 
    string? Detail = null, 
    string? Target = null,
    IList<System.ComponentModel.DataAnnotations.ValidationResult>? ValidationErrors = null)
{
    /// <summary>
    /// Represents no error (successful operation).
    /// </summary>
    public readonly static Error None = new("none", "none");

    /// <summary>
    /// Creates a validation error.
    /// Used for input validation failures, business rule violations.
    /// Maps to HTTP 400 Bad Request.
    /// </summary>
    /// <param name="code">Specific validation error code</param>
    /// <param name="message">Error message</param>
    /// <param name="target">The field or property that failed validation</param>
    public static Error Validation(string code, string? message = null, string? target = null) 
        => new("validation", $"{code}", message, Target: target);

    /// <summary>
    /// Creates a validation error with detailed field-level validation results.
    /// Used for schema validation or complex validation scenarios.
    /// Maps to HTTP 400 Bad Request.
    /// </summary>
    /// <param name="code">Specific validation error code</param>
    /// <param name="message">Error message</param>
    /// <param name="validationError">Collection of detailed validation error</param>
    /// <param name="target">The field or property that failed validation</param>
    public static Error Validation(
        string code, 
        string? message, 
        AetherValidationException validationError, 
        string? target = null)
        => new("validation",$"{code}", message ?? validationError.Message, Target: target, ValidationErrors: validationError.ValidationErrors);
    
    /// <summary>
    /// Creates a validation error with detailed field-level validation results.
    /// Used for schema validation or complex validation scenarios.
    /// Maps to HTTP 400 Bad Request.
    /// </summary>
    /// <param name="code">Specific validation error code</param>
    /// <param name="message">Error message</param>
    /// <param name="validationErrors">Collection of detailed validation errors</param>
    /// <param name="target">The field or property that failed validation</param>
    public static Error Validation(
        string code, 
        string? message, 
        IList<System.ComponentModel.DataAnnotations.ValidationResult> validationErrors, 
        string? target = null)
        => new("validation", $"{code}", message, Target: target, ValidationErrors: validationErrors);

    /// <summary>
    /// Creates a conflict error.
    /// Used when an operation conflicts with current state (e.g., duplicate key).
    /// Maps to HTTP 409 Conflict.
    /// </summary>
    /// <param name="code">Specific conflict error code</param>
    /// <param name="message">Error message</param>
    /// <param name="target">The resource that conflicts</param>
    public static Error Conflict(string code, string? message = null, string? target = null) 
        => new("conflict", $"{code}", message, Target: target);

    /// <summary>
    /// Creates a not found error.
    /// Used when a requested resource does not exist.
    /// Maps to HTTP 404 Not Found.
    /// </summary>
    /// <param name="code">Specific not found error code</param>
    /// <param name="message">Error message</param>
    /// <param name="target">The resource identifier that was not found</param>
    public static Error NotFound(string code, string? message = null, string? target = null) 
        => new("notfound", $"{code}", message, Target: target);

    /// <summary>
    /// Creates an unauthorized error.
    /// Used when authentication is required but missing or invalid.
    /// Maps to HTTP 401 Unauthorized.
    /// </summary>
    /// <param name="code">Specific authorization error code</param>
    /// <param name="message">Error message</param>
    public static Error Unauthorized(string code = "unauthorized", string? message = null) 
        => new("unauthorized",$"{code}", message);

    /// <summary>
    /// Creates a forbidden error.
    /// Used when the user is authenticated but lacks permission.
    /// Maps to HTTP 403 Forbidden.
    /// </summary>
    /// <param name="code">Specific permission error code</param>
    /// <param name="message">Error message</param>
    public static Error Forbidden(string code = "forbidden", string? message = null) 
        => new("forbidden", $"{code}", message);

    /// <summary>
    /// Creates a dependency error.
    /// Used when an external dependency (database, service) fails unexpectedly.
    /// Maps to HTTP 502 Bad Gateway.
    /// </summary>
    /// <param name="code">Specific dependency error code</param>
    /// <param name="message">Error message</param>
    /// <param name="target">The dependency that failed</param>
    public static Error Dependency(string code, string? message = null, string? target = null)
        => new("dependency", $"{code}", message, Target: target);

    /// <summary>
    /// Creates a transient error.
    /// Used for temporary failures that might succeed on retry (timeouts, cancellations).
    /// Maps to HTTP 503 Service Unavailable.
    /// </summary>
    /// <param name="code">Specific transient error code</param>
    /// <param name="message">Error message</param>
    /// <param name="target">The operation that failed transiently</param>
    public static Error Transient(string code, string? message = null, string? target = null)
        => new("transient", $"{code}", message, Target: target);

    /// <summary>
    /// Creates a general failure error.
    /// Used for unexpected errors that don't fit other categories.
    /// Maps to HTTP 500 Internal Server Error.
    /// </summary>
    /// <param name="code">Specific error code</param>
    /// <param name="message">Error message</param>
    /// <param name="detail">Additional error details</param>
    public static Error Failure(string code, string? message = null, string? detail = null)
        => new("failure", $"{code}", message, detail);
}

