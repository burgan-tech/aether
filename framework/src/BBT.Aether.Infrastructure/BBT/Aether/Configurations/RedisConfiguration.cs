using System.Collections.Generic;

namespace BBT.Aether.Configurations;

/// <summary>
/// Represents the configuration for Redis.
/// </summary>
public class RedisConfiguration
{
    /// <summary>
    /// Gets or sets the mode of Redis (e.g., Standalone, Cluster, Sentinel).
    /// </summary>
    public string Mode { get; set; } = "Standalone";
    /// <summary>
    /// Gets or sets the instance name of Redis.
    /// </summary>
    public string InstanceName { get; set; } = "default";
    /// <summary>
    /// Gets or sets the connection timeout in milliseconds.
    /// </summary>
    public int ConnectionTimeout { get; set; } = 5000;
    /// <summary>
    /// Gets or sets the default database to use.
    /// </summary>
    public int DefaultDatabase { get; set; } = 0;
    /// <summary>
    /// Gets or sets the password for Redis.
    /// </summary>
    public string Password { get; set; } = "";
    /// <summary>
    /// Gets or sets a value indicating whether SSL is enabled.
    /// </summary>
    public bool Ssl { get; set; } = false;
    /// <summary>
    /// Gets or sets the configuration for standalone mode.
    /// </summary>
    public StandaloneConfig Standalone { get; set; } = new();
    /// <summary>
    /// Gets or sets the configuration for cluster mode.
    /// </summary>
    public ClusterConfig Cluster { get; set; } = new();
    /// <summary>
    /// Gets or sets the configuration for sentinel mode.
    /// </summary>
    public SentinelConfig Sentinel { get; set; } = new();
    /// <summary>
    /// Gets or sets the retry policy configuration.
    /// </summary>
    public RetryPolicyConfig RetryPolicy { get; set; } = new();
}

/// <summary>
/// Represents the configuration for standalone Redis mode.
/// </summary>
public class StandaloneConfig
{
    /// <summary>
    /// Gets or sets the list of endpoints for the standalone Redis instance.
    /// </summary>
    public List<string> EndPoints { get; set; } = new();
}

/// <summary>
/// Represents the configuration for Redis cluster mode.
/// </summary>
public class ClusterConfig
{
    /// <summary>
    /// Gets or sets the list of endpoints for the Redis cluster.
    /// </summary>
    public List<string> EndPoints { get; set; } = new();
    /// <summary>
    /// Gets or sets the maximum number of redirects.
    /// </summary>
    public int MaxRedirects { get; set; } = 3;
}

/// <summary>
/// Represents the configuration for Redis sentinel mode.
/// </summary>
public class SentinelConfig
{
    /// <summary>
    /// Gets or sets the list of master names.
    /// </summary>
    public List<string> Masters { get; set; } = new();
    /// <summary>
    /// Gets or sets the list of sentinel endpoints.
    /// </summary>
    public List<string> Sentinels { get; set; } = new();
    /// <summary>
    /// Gets or sets the default database to use.
    /// </summary>
    public int DefaultDatabase { get; set; } = 0;
}

/// <summary>
/// Represents the retry policy configuration.
/// </summary>
public class RetryPolicyConfig
{
    /// <summary>
    /// Gets or sets the maximum number of retries.
    /// </summary>
    public int MaxRetries { get; set; } = 3;
    /// <summary>
    /// Gets or sets the retry timeout in milliseconds.
    /// </summary>
    public int RetryTimeout { get; set; } = 1000;
}