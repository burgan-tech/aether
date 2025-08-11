using System;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Guids;
using BBT.Aether.Mapper;
using BBT.Aether.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Aether.Application.Services;

public abstract class ApplicationService(IServiceProvider serviceProvider)
    : IApplicationService, IServiceProviderAccessor
{
    public IServiceProvider ServiceProvider { get; } = serviceProvider;

    public ILazyServiceProvider LazyServiceProvider { get; } =
        serviceProvider.GetRequiredService<ILazyServiceProvider>();
    
    protected ICurrentUser CurrentUser => LazyServiceProvider.LazyGetRequiredService<ICurrentUser>();
    protected IGuidGenerator GuidGenerator => LazyServiceProvider.LazyGetRequiredService<IGuidGenerator>();
    protected IObjectMapper ObjectMapper => LazyServiceProvider.LazyGetRequiredService<IObjectMapper>();
    
    protected ILoggerFactory LoggerFactory => LazyServiceProvider.LazyGetRequiredService<ILoggerFactory>();
    protected ILogger Logger => LoggerFactory?.CreateLogger(GetType().FullName!) ?? NullLogger.Instance;
}