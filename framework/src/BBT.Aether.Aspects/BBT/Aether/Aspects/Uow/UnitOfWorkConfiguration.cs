using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using BBT.Aether.Uow;

namespace BBT.Aether.Aspects;

/// <summary>
/// Configuration for automatic UnitOfWork aspect application.
/// Allows customization of default UnitOfWork behavior when auto-applied through UnitOfWorkAspectProvider.
/// </summary>
public class UnitOfWorkConfiguration
{
    /// <summary>
    /// Gets or sets whether automatically applied UnitOfWork should be transactional.
    /// Default is false.
    /// </summary>
    public bool IsTransactional { get; set; } = false;

    /// <summary>
    /// Gets or sets the default scope for automatically applied UnitOfWork.
    /// Default is Required.
    /// </summary>
    public UnitOfWorkScopeOption Scope { get; set; } = UnitOfWorkScopeOption.Required;

    /// <summary>
    /// Gets or sets the default isolation level for transactional UnitOfWork.
    /// Default is null (uses provider default).
    /// </summary>
    public IsolationLevel? IsolationLevel { get; set; }

    /// <summary>
    /// Gets or sets a callback to configure UnitOfWork per method.
    /// Allows fine-grained control over aspect configuration based on method metadata.
    /// </summary>
    public Action<MethodInfo, UnitOfWorkAttribute>? ConfigureMethod { get; set; }

    /// <summary>
    /// Collection of method name patterns to exclude from automatic UnitOfWork.
    /// Supports simple wildcard patterns (e.g., "Get*", "*Async").
    /// </summary>
    public HashSet<string> ExcludedMethodPatterns { get; } = new();

    /// <summary>
    /// Collection of exact method names to exclude from automatic UnitOfWork.
    /// </summary>
    public HashSet<string> ExcludedMethodNames { get; } = new();

    /// <summary>
    /// Checks if a method should be excluded from automatic UnitOfWork based on configuration.
    /// </summary>
    internal bool ShouldExcludeMethod(MethodInfo method)
    {
        // Check exact name match
        if (ExcludedMethodNames.Contains(method.Name))
            return true;

        // Check pattern match
        foreach (var pattern in ExcludedMethodPatterns)
        {
            if (MatchesPattern(method.Name, pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Simple wildcard pattern matching.
    /// Supports * at the beginning and/or end of pattern.
    /// </summary>
    private bool MatchesPattern(string methodName, string pattern)
    {
        if (pattern == "*")
            return true;

        if (pattern.StartsWith("*") && pattern.EndsWith("*"))
        {
            var middle = pattern.Substring(1, pattern.Length - 2);
            return methodName.Contains(middle);
        }

        if (pattern.StartsWith("*"))
        {
            var suffix = pattern.Substring(1);
            return methodName.EndsWith(suffix);
        }

        if (pattern.EndsWith("*"))
        {
            var prefix = pattern.Substring(0, pattern.Length - 1);
            return methodName.StartsWith(prefix);
        }

        return methodName == pattern;
    }

    /// <summary>
    /// Adds a method name pattern to exclude from automatic UnitOfWork.
    /// </summary>
    public UnitOfWorkConfiguration ExcludeMethodPattern(string pattern)
    {
        ExcludedMethodPatterns.Add(pattern);
        return this;
    }

    /// <summary>
    /// Adds an exact method name to exclude from automatic UnitOfWork.
    /// </summary>
    public UnitOfWorkConfiguration ExcludeMethod(string methodName)
    {
        ExcludedMethodNames.Add(methodName);
        return this;
    }

    /// <summary>
    /// Configures the UnitOfWork as transactional with optional isolation level.
    /// </summary>
    public UnitOfWorkConfiguration AsTransactional(IsolationLevel? isolationLevel = null)
    {
        IsTransactional = true;
        IsolationLevel = isolationLevel;
        return this;
    }

    /// <summary>
    /// Sets the scope option for automatically applied UnitOfWork.
    /// </summary>
    public UnitOfWorkConfiguration WithScope(UnitOfWorkScopeOption scope)
    {
        Scope = scope;
        return this;
    }
}

