using System;

namespace BBT.Aether;

public interface IApplicationInfoAccessor
{
    /// <summary>
    /// Name of the application.
    /// This is useful for systems with multiple applications, to distinguish
    /// resources of the applications located together.
    /// </summary>
    string? ApplicationName { get; }

    /// <summary>
    /// A unique identifier for this application instance.
    /// This value changes whenever the application is restarted.
    /// </summary>
    string InstanceId { get; }
    
    /// <summary>
    /// Deployment Id
    /// </summary>
    string DeploymentId { get; }
}

public class ApplicationInfoAccessor(string? applicationName, string instanceId) : IApplicationInfoAccessor
{
    public string? ApplicationName { get; } = applicationName;
    public string InstanceId { get; } = instanceId;
    public string DeploymentId { get; } = $"{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}-{applicationName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{instanceId}";
}