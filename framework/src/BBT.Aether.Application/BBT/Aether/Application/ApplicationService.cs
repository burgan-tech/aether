using System;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Guids;
using BBT.Aether.Mapper;
using BBT.Aether.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Aether.Application.Services;

public abstract class ApplicationService : IApplicationService, IServiceProviderAccessor
{
    private readonly IServiceProvider? _explicitServiceProvider;

    /// <summary>
    /// Initializes a new instance with explicit service provider (recommended for DI scenarios).
    /// </summary>
    protected ApplicationService(IServiceProvider serviceProvider)
    {
        _explicitServiceProvider = serviceProvider;
    }

    /// <summary>
    /// Initializes a new instance relying on AmbientServiceProvider (for aspect-oriented scenarios).
    /// </summary>
    protected ApplicationService()
    {
        _explicitServiceProvider = null;
    }

    public IServiceProvider ServiceProvider =>
        _explicitServiceProvider
        ?? AmbientServiceProvider.Current
        ?? AmbientServiceProvider.Root
        ?? throw new InvalidOperationException(
            "No service provider available. Either inject IServiceProvider in constructor or ensure AmbientServiceProvider is configured.");

    public ILazyServiceProvider LazyServiceProvider =>
        ServiceProvider.GetRequiredService<ILazyServiceProvider>();
    
    protected ICurrentUser CurrentUser => LazyServiceProvider.LazyGetRequiredService<ICurrentUser>();
    protected IGuidGenerator GuidGenerator => LazyServiceProvider.LazyGetRequiredService<IGuidGenerator>();
    protected IObjectMapper ObjectMapper => LazyServiceProvider.LazyGetRequiredService<IObjectMapper>();
    
    protected ILoggerFactory LoggerFactory => LazyServiceProvider.LazyGetRequiredService<ILoggerFactory>();
    protected ILogger Logger => LoggerFactory?.CreateLogger(GetType().FullName!) ?? NullLogger.Instance;
}