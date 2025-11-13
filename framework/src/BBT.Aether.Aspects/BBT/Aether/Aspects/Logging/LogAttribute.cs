using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PostSharp.Aspects;
using PostSharp.Aspects.Dependencies;
using PostSharp.Extensibility;
using PostSharp.Serialization;

namespace BBT.Aether.Aspects;

/// <summary>
/// Aspect that automatically logs method entry, exit, and exceptions with performance tracking and enrichment.
/// Uses structured logging with BeginScope for proper context propagation and telemetry integration.
/// Execution order: Runs AFTER Trace, BEFORE UnitOfWork (middle layer).
/// </summary>
[PSerializable]
[AspectTypeDependency(AspectDependencyAction.Order, AspectDependencyPosition.After, typeof(TraceAttribute))]
[MulticastAttributeUsage(
    MulticastTargets.Method,
    AllowMultiple = false,
    Inheritance = MulticastInheritance.Strict)]
public class LogAttribute : AetherMethodInterceptionAspect
{
    /// <summary>
    /// Shared JSON serializer options for consistent and performant serialization.
    /// </summary>
    private readonly static JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 3 // Limit depth to avoid circular references
    };

    /// <summary>
    /// Gets or sets the log level for method entry/exit logging.
    /// Default is Information.
    /// </summary>
    public LogLevel Level { get; set; } = LogLevel.Information;

    /// <summary>
    /// Gets or sets whether method arguments should be logged.
    /// Default is false for privacy and security concerns.
    /// </summary>
    public bool LogArguments { get; set; } = false;

    /// <summary>
    /// Gets or sets whether the method return value should be logged.
    /// Default is false for privacy and security concerns.
    /// </summary>
    public bool LogReturnValue { get; set; } = false;

    /// <summary>
    /// Intercepts async method execution to add structured logging with performance tracking.
    /// </summary>
    public async override Task OnInvokeAsync(MethodInterceptionArgs args)
    {
        // Get logger from service provider
        var serviceProvider = GetServiceProvider();
        var loggerType = typeof(ILogger<>).MakeGenericType(args.Method.DeclaringType ?? typeof(object));
        var logger = (ILogger)serviceProvider.GetRequiredService(loggerType);

        // Skip logging if the configured level is not enabled
        if (!logger.IsEnabled(Level))
        {
            await args.ProceedAsync();
            return;
        }

        // Create enrichment data
        var enrichmentData = CreateEnrichmentData(args);
        var methodName = GetMethodName(args);
        var className = GetClassName(args);

        // Start performance measurement
        var stopwatch = Stopwatch.StartNew();

        // Begin structured logging scope
        using (logger.BeginScope(enrichmentData))
        {
            try
            {
                // Log method entry
                LogMethodEntry(logger, methodName, className, enrichmentData);

                // Execute the method
                await args.ProceedAsync();

                // Stop performance measurement
                stopwatch.Stop();

                // Add execution time to enrichment
                enrichmentData["ExecutionTimeMs"] = stopwatch.ElapsedMilliseconds;

                // Add return value if enabled
                if (LogReturnValue && args.ReturnValue != null)
                {
                    enrichmentData["ReturnValue"] = SerializeValue(args.ReturnValue);
                }

                // Log method exit
                LogMethodExit(logger, methodName, className, stopwatch.ElapsedMilliseconds);

                // Call extensibility point
                await OnAfterAsync(args);
            }
            catch (Exception ex)
            {
                // Stop performance measurement
                stopwatch.Stop();

                // Add execution time to enrichment
                enrichmentData["ExecutionTimeMs"] = stopwatch.ElapsedMilliseconds;

                // Log exception with context
                LogMethodException(logger, methodName, className, ex, stopwatch.ElapsedMilliseconds);

                // Call extensibility point
                await OnExceptionAsync(args, ex);

                // Re-throw the exception
                throw;
            }
        }
    }

    /// <summary>
    /// Intercepts synchronous method execution to add structured logging with performance tracking.
    /// Bridges sync methods to async logging implementation.
    /// </summary>
    public override void OnInvoke(MethodInterceptionArgs args)
    {
        // Bridge synchronous method to async logging handling
        OnInvokeAsync(args).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Creates enrichment data dictionary with method and class metadata.
    /// Override this method to add custom enrichment data.
    /// </summary>
    protected virtual Dictionary<string, object> CreateEnrichmentData(MethodInterceptionArgs args)
    {
        var enrichmentData = new Dictionary<string, object>
        {
            ["MethodName"] = GetMethodName(args),
            ["ClassName"] = GetClassName(args),
            ["Timestamp"] = DateTimeOffset.UtcNow
        };

        var parameters = args.Method.GetParameters();

        // Process parameters for [Enrich] attribute
        for (int i = 0; i < parameters.Length && i < args.Arguments.Count; i++)
        {
            var parameter = parameters[i];
            var enrichAttr = parameter.GetCustomAttributes(typeof(EnrichAttribute), true)
                .FirstOrDefault() as EnrichAttribute;

            if (enrichAttr != null)
            {
                var paramValue = args.Arguments[i];
                var enrichmentName = !string.IsNullOrWhiteSpace(enrichAttr.Name) 
                    ? enrichAttr.Name 
                    : parameter.Name ?? $"arg{i}";

                enrichmentData[enrichmentName] = SerializeValue(paramValue);
            }
        }

        // Add all arguments if enabled
        if (LogArguments && args.Arguments.Count > 0)
        {
            var argumentsData = new Dictionary<string, object>();

            for (int i = 0; i < parameters.Length && i < args.Arguments.Count; i++)
            {
                var paramName = parameters[i].Name ?? $"arg{i}";
                var paramValue = args.Arguments[i];

                if (paramValue != null)
                {
                    argumentsData[paramName] = SerializeValue(paramValue);
                }
            }

            if (argumentsData.Count > 0)
            {
                // Serialize the entire arguments dictionary to a JSON string
                // This ensures it's properly formatted in logs instead of showing the type name
                try
                {
                    enrichmentData["Arguments"] = JsonSerializer.Serialize(argumentsData, SerializerOptions);
                }
                catch
                {
                    // Fallback: if serialization fails, keep the dictionary as-is
                    enrichmentData["Arguments"] = argumentsData;
                }
            }
        }

        return enrichmentData;
    }

    /// <summary>
    /// Gets the simple method name without parameters or generic arguments.
    /// </summary>
    private string GetMethodName(MethodInterceptionArgs args)
    {
        return args.Method.Name;
    }

    /// <summary>
    /// Gets the class name including namespace.
    /// </summary>
    private string GetClassName(MethodInterceptionArgs args)
    {
        var declaringType = args.Method.DeclaringType;
        return declaringType?.FullName ?? declaringType?.Name ?? "Unknown";
    }

    /// <summary>
    /// Logs method entry with the configured log level.
    /// </summary>
    private void LogMethodEntry(ILogger logger, string methodName, string className, Dictionary<string, object> enrichmentData)
    {
        var message = $"Entering method: {className}.{methodName}";
        logger.Log(Level, message);
    }

    /// <summary>
    /// Logs method exit with execution time.
    /// </summary>
    private void LogMethodExit(ILogger logger, string methodName, string className, long executionTimeMs)
    {
        var message = $"Exiting method: {className}.{methodName} (took {executionTimeMs}ms)";
        logger.Log(Level, message);
    }

    /// <summary>
    /// Logs method exception with Error level and execution context.
    /// </summary>
    private void LogMethodException(ILogger logger, string methodName, string className, Exception ex, long executionTimeMs)
    {
        var message = $"Exception in method: {className}.{methodName} (took {executionTimeMs}ms before failure)";
        logger.LogError(ex, message);
    }

    /// <summary>
    /// Serializes a value to a safe string representation for logging.
    /// Handles common types and falls back to JSON serialization.
    /// </summary>
    protected string SerializeValue(object value)
    {
        if (value == null)
        {
            return "null";
        }

        var type = value.GetType();

        // Handle primitive types and strings directly
        if (type.IsPrimitive || value is string || value is DateTime || value is DateTimeOffset || value is Guid)
        {
            return value.ToString() ?? "null";
        }

        // Serialize complex types to JSON using shared options
        try
        {
            return JsonSerializer.Serialize(value, SerializerOptions);
        }
        catch
        {
            // Fallback to ToString if serialization fails
            return value.ToString() ?? type.Name;
        }
    }
}

