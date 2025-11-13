namespace BBT.Aether.ExceptionHandling;

public class AetherExceptionHandlingOptions
{
    public bool SendExceptionsDetailsToClients { get; set; } = false;

    public bool SendStackTraceToClients { get; set; } = true;
    
    /// <summary>
    /// Base URL for error type references in ProblemDetails.
    /// Defaults to "https://httpstatuses.com" but can be customized for application-specific error documentation.
    /// </summary>
    public string ErrorTypeBaseUrl { get; set; } = "https://httpstatuses.com";
}