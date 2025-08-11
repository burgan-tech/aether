namespace BBT.Aether.ExceptionHandling;

public class AetherExceptionHandlingOptions
{
    public bool SendExceptionsDetailsToClients { get; set; } = false;

    public bool SendStackTraceToClients { get; set; } = true;
}