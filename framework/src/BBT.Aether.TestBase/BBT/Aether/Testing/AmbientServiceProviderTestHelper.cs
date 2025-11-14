using System;
using BBT.Aether.DependencyInjection;

namespace BBT.Aether.Testing;

/// <summary>
/// Helper class for setting up AmbientServiceProvider in unit tests.
/// </summary>
public static class AmbientServiceProviderTestHelper
{
    /// <summary>
    /// Sets the AmbientServiceProvider.Current for the duration of the returned IDisposable.
    /// Restores the previous value when disposed.
    /// </summary>
    /// <param name="serviceProvider">The service provider to set as current</param>
    /// <returns>An IDisposable that will restore the previous service provider when disposed</returns>
    /// <example>
    /// <code>
    /// [Fact]
    /// public void MyTest()
    /// {
    ///     using var _ = AmbientServiceProviderTestHelper.SetScoped(mockServiceProvider);
    ///     // Test code here - AmbientServiceProvider.Current is now mockServiceProvider
    /// }
    /// // Previous value is automatically restored here
    /// </code>
    /// </example>
    public static IDisposable SetScoped(IServiceProvider serviceProvider)
    {
        var previous = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = serviceProvider;
        return new DisposableAction(() => AmbientServiceProvider.Current = previous);
    }

    /// <summary>
    /// Sets the AmbientServiceProvider.Root for the duration of the returned IDisposable.
    /// Restores the previous value when disposed.
    /// </summary>
    /// <param name="serviceProvider">The service provider to set as root</param>
    /// <returns>An IDisposable that will restore the previous root service provider when disposed</returns>
    public static IDisposable SetRoot(IServiceProvider serviceProvider)
    {
        var previous = AmbientServiceProvider.Root;
        AmbientServiceProvider.Root = serviceProvider;
        return new DisposableAction(() => AmbientServiceProvider.Root = previous);
    }

    /// <summary>
    /// Clears both Current and Root AmbientServiceProvider values for the duration of the returned IDisposable.
    /// Restores the previous values when disposed.
    /// </summary>
    /// <returns>An IDisposable that will restore the previous values when disposed</returns>
    public static IDisposable Clear()
    {
        var previousCurrent = AmbientServiceProvider.Current;
        var previousRoot = AmbientServiceProvider.Root;
        
        AmbientServiceProvider.Current = null;
        AmbientServiceProvider.Root = null;
        
        return new DisposableAction(() =>
        {
            AmbientServiceProvider.Current = previousCurrent;
            AmbientServiceProvider.Root = previousRoot;
        });
    }

    private sealed class DisposableAction : IDisposable
    {
        private readonly Action _action;
        private bool _disposed;

        public DisposableAction(Action action)
        {
            _action = action ?? throw new ArgumentNullException(nameof(action));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _action();
                _disposed = true;
            }
        }
    }
}

