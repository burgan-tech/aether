# Automatic UnitOfWork Application

## Overview

The automatic UnitOfWork feature allows you to apply `[UnitOfWork]` aspect to all methods of classes implementing specific marker interfaces (like `IApplicationService`) without manually adding the attribute to every method.

## Basic Usage

### Step 1: Enable Automatic UnitOfWork

Add the following attribute to any `.cs` file in your project (commonly in `AssemblyInfo.cs` or at the top of `Program.cs`):

```csharp
using BBT.Aether.Aspects;

[assembly: AutoUnitOfWork]
```

### Step 2: Create Your Application Service

```csharp
public interface IIssueAppService : IApplicationService
{
    Task<IssueDto> CreateAsync(CreateIssueDto input);
    Task<IssueDto> GetAsync(Guid id);
    Task UpdateAsync(Guid id, UpdateIssueDto input);
    Task DeleteAsync(Guid id);
}

public class IssueAppService : ApplicationService, IIssueAppService
{
    private readonly IRepository<Issue, Guid> _repository;

    public IssueAppService(
        IServiceProvider serviceProvider,
        IRepository<Issue, Guid> repository) 
        : base(serviceProvider)
    {
        _repository = repository;
    }

    // ✅ UnitOfWork automatically applied - no attribute needed!
    public async Task<IssueDto> CreateAsync(CreateIssueDto input)
    {
        var issue = ObjectMapper.Map<Issue>(input);
        await _repository.InsertAsync(issue);
        return ObjectMapper.Map<IssueDto>(issue);
    }

    // ✅ UnitOfWork automatically applied
    public async Task<IssueDto> GetAsync(Guid id)
    {
        var issue = await _repository.GetAsync(id);
        return ObjectMapper.Map<IssueDto>(issue);
    }

    // ✅ UnitOfWork automatically applied
    public async Task UpdateAsync(Guid id, UpdateIssueDto input)
    {
        var issue = await _repository.GetAsync(id);
        ObjectMapper.Map(input, issue);
        await _repository.UpdateAsync(issue);
    }

    // ✅ UnitOfWork automatically applied
    public async Task DeleteAsync(Guid id)
    {
        await _repository.DeleteAsync(id);
    }
}
```

### Step 3: No More Manual Attributes!

That's it! All public methods of `IssueAppService` will automatically have `[UnitOfWork]` applied because the class implements `IApplicationService`.

## Advanced Configuration

### Configure Default Behavior

You can customize the default UnitOfWork behavior in your application startup:

```csharp
using BBT.Aether.Aspects;

// In Program.cs or Startup.cs
UnitOfWorkConfigurationExtensions.ConfigureAutoUnitOfWork(config =>
{
    // Make all auto-applied UnitOfWork transactional
    config.AsTransactional(IsolationLevel.ReadCommitted);
    
    // Or use non-transactional (default)
    config.IsTransactional = false;
    
    // Set scope option
    config.WithScope(UnitOfWorkScopeOption.Required);
    
    // Exclude read-only methods from UnitOfWork
    config.ExcludeMethodPattern("Get*");
    config.ExcludeMethodPattern("Find*");
    config.ExcludeMethod("CountAsync");
    
    // Custom configuration per method
    config.ConfigureMethod = (method, aspect) =>
    {
        // Make Create/Update/Delete methods transactional
        if (method.Name.StartsWith("Create") || 
            method.Name.StartsWith("Update") || 
            method.Name.StartsWith("Delete"))
        {
            aspect.IsTransactional = true;
            aspect.IsolationLevel = IsolationLevel.ReadCommitted;
        }
    };
});
```

### Exclude Specific Methods

You can still override automatic behavior using attributes:

```csharp
public class IssueAppService : ApplicationService, IIssueAppService
{
    // ✅ Uses automatic UnitOfWork with default settings
    public async Task<IssueDto> CreateAsync(CreateIssueDto input)
    {
        // ...
    }

    // ✅ Override with custom settings
    [UnitOfWork(IsTransactional = true, IsolationLevel = IsolationLevel.Serializable)]
    public async Task UpdateAsync(Guid id, UpdateIssueDto input)
    {
        // This method uses explicit settings, ignoring automatic configuration
    }

    // ❌ Disable UnitOfWork for this specific method
    [UnitOfWork(Scope = UnitOfWorkScopeOption.Suppress)]
    public async Task<IssueDto> GetAsync(Guid id)
    {
        // No UnitOfWork here
    }
}
```

### Register Custom Marker Interfaces

You can register additional marker interfaces for automatic UnitOfWork:

```csharp
using BBT.Aether.Aspects;

// In Program.cs or Startup.cs
UnitOfWorkConfigurationExtensions.RegisterUnitOfWorkMarkerInterface<IDomainService>();
UnitOfWorkConfigurationExtensions.RegisterUnitOfWorkMarkerInterface<ICommandHandler>();

// Now any class implementing IDomainService or ICommandHandler 
// will automatically have UnitOfWork applied
```

### Assembly-Level Configuration

You can also configure some settings via the assembly attribute:

```csharp
// Make all auto-applied UnitOfWork transactional
[assembly: AutoUnitOfWork(IsTransactional = true)]

// Or include only write methods
[assembly: AutoUnitOfWork(IncludeReadOnlyMethods = false)]
```

## How It Works

1. **Compile-Time Weaving**: PostSharp's `IAspectProvider` analyzes your code during compilation
2. **Interface Detection**: It finds all classes implementing `IApplicationService` (or registered marker interfaces)
3. **Aspect Application**: It automatically applies `[UnitOfWork]` to all public methods of those classes
4. **No Runtime Overhead**: The aspects are woven into IL at compile-time, no reflection at runtime

## Benefits

✅ **DRY Principle**: No need to repeat `[UnitOfWork]` on every method  
✅ **Convention Over Configuration**: All application services follow the same pattern  
✅ **Type-Safe**: Works with strongly-typed interfaces  
✅ **Flexible**: Can still override behavior with explicit attributes  
✅ **Performant**: No runtime overhead, compile-time weaving  

## Migration from Manual Attributes

If you have existing services with manual `[UnitOfWork]` attributes:

1. **Keep them as-is**: Explicit attributes take precedence over automatic application
2. **Remove gradually**: You can safely remove `[UnitOfWork]` attributes from methods - they'll be auto-applied
3. **Test thoroughly**: Verify UnitOfWork behavior remains the same after migration

## Troubleshooting

### UnitOfWork Not Applied?

Check:
- ✓ `[assembly: AutoUnitOfWork]` is present in your project
- ✓ Your class implements `IApplicationService` (or a registered marker interface)
- ✓ The method is `public` (protected/private methods are not affected)
- ✓ PostSharp build is successful (check build output)

### Wrong Configuration?

- Explicit `[UnitOfWork]` attributes always override automatic configuration
- Check `ConfigureAutoUnitOfWork` is called before your services are used
- Use debugger to verify aspect is applied correctly

## Example Project Structure

```
MyProject/
├── AssemblyInfo.cs                          // [assembly: AutoUnitOfWork]
├── Program.cs                               // ConfigureAutoUnitOfWork()
└── ApplicationServices/
    ├── Issues/
    │   ├── IIssueAppService.cs             // : IApplicationService
    │   └── IssueAppService.cs              // ✅ Auto UnitOfWork
    ├── Projects/
    │   ├── IProjectAppService.cs           // : IApplicationService
    │   └── ProjectAppService.cs            // ✅ Auto UnitOfWork
    └── Users/
        ├── IUserAppService.cs              // : IApplicationService
        └── UserAppService.cs               // ✅ Auto UnitOfWork
```

All services automatically get UnitOfWork without any manual attributes! 🎉

