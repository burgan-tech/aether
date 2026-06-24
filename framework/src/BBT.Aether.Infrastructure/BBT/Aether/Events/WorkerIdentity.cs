using System;
using Microsoft.Extensions.Hosting;

namespace BBT.Aether.Events;

/// <summary>
/// Provides a stable, structured identity for this worker instance.
/// Format: {appName}/{podName}/{processId}/{instanceId}
/// </summary>
public sealed class WorkerIdentity
{
    public string Value { get; }

    public WorkerIdentity(IHostEnvironment env)
    {
        var pod = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;
        var instanceId = Guid.NewGuid().ToString("N")[..8];
        Value = $"{env.ApplicationName}/{pod}/{Environment.ProcessId}/{instanceId}";
    }
}
