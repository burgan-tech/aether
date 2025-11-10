# Unit of Work Architecture - Full Technical Documentation

## 1. Overview
This document details the full implementation of the **Unit of Work (UoW)** pattern combined with **Repository** abstraction and **PostSharp AOP** aspect for declarative transaction management.  
The architecture supports multiple data providers (**EF Core**, **Dapper**, **MongoDB**) and ensures isolation, composability, and transactional consistency.  
It also eliminates the need for ref-counting through scope-based participation.

---

## 2. Objectives
- Provide consistent transaction management across EF Core, Dapper, and MongoDB.  
- Allow ambient UoW propagation using AsyncLocal.  
- Support nested scopes without ref-counting using participation semantics.  
- Enable declarative UoW management via PostSharp Aspect.  
- Ensure clear rollback/commit boundaries and isolation.

---

## 3. AsyncLocalAmbientUowAccessor
Responsible for storing and retrieving the current Unit of Work context within the asynchronous call chain.  
This allows propagation of the same UoW across middleware → service → repository without passing it explicitly.

```csharp
public sealed class AsyncLocalAmbientUowAccessor : IAmbientUnitOfWorkAccessor
{
    private static readonly AsyncLocal<IUnitOfWork?> _current = new();
    public IUnitOfWork? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
```

---

## 4. CompositeUnitOfWork
Handles multiple data source transactions in a coordinated manner.  
Each data provider registers an `ILocalTransactionSource`, and `CompositeUnitOfWork` initializes and commits/rolls back each provider transaction in order.

```csharp
public sealed class CompositeUnitOfWork : IAsyncDisposable
{
    private readonly IEnumerable<ILocalTransactionSource> _sources;
    private readonly List<ILocalTransaction> _opened = new();
    public bool IsAborted { get; private set; }
    public bool IsCompleted { get; private set; }

    public async Task InitializeAsync(UnitOfWorkOptions options, CancellationToken ct = default)
    {
        foreach (var s in _sources)
            _opened.Add(await s.CreateTransactionAsync(options, ct));
    }

    public void Abort() => IsAborted = true;

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (IsAborted)
            throw new InvalidOperationException("Aborted by inner UoW scope.");
        foreach (var t in _opened)
            await t.CommitAsync(ct);
        IsCompleted = true;
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        for (int i = _opened.Count - 1; i >= 0; i--)
        {
            try { await _opened[i].RollbackAsync(ct); } catch { }
        }
        IsCompleted = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsCompleted)
            await RollbackAsync();
    }
}
```

---

## 5. UnitOfWorkManager
Manages creation and participation of UoW scopes.  
Handles `Required`, `RequiresNew`, and `Suppress` semantics without ref-counting.

```csharp
public sealed class UnitOfWorkManager : IUnitOfWorkManager
{
    private readonly IAmbientUnitOfWorkAccessor _ambient;
    private readonly IServiceProvider _sp;

    public UnitOfWorkManager(IAmbientUnitOfWorkAccessor ambient, IServiceProvider sp)
    {
        _ambient = ambient;
        _sp = sp;
    }

    public IUnitOfWork? Current => _ambient.Current;

    public async Task<IUnitOfWork> BeginAsync(UnitOfWorkOptions? options = null, CancellationToken ct = default)
    {
        options ??= new UnitOfWorkOptions();
        if (options.Scope == UnitOfWorkScopeOption.Suppress)
            return new SuppressedUowScope(_ambient);

        var existing = _ambient.Current as UnitOfWorkScope;

        if (options.Scope == UnitOfWorkScopeOption.Required && existing != null)
            return new UnitOfWorkScope(existing.Root, false, _ambient);

        var sources = _sp.GetServices<ILocalTransactionSource>();
        var root = new CompositeUnitOfWork(sources);
        await root.InitializeAsync(options, ct);
        return new UnitOfWorkScope(root, true, _ambient);
    }
}
```

---

## 6. EFCoreTransactionSource
Creates and manages EF Core database transactions within the UoW scope.  
Allows Dapper to share the same connection and transaction.

```csharp
public sealed class EfCoreTransactionSource<TDbContext> : ILocalTransactionSource
    where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;
    private IDbContextTransaction? _tx;

    public EfCoreTransactionSource(TDbContext dbContext) => _dbContext = dbContext;
    public string SourceName => $"efcore:{typeof(TDbContext).Name}";

    public async Task<ILocalTransaction> CreateTransactionAsync(UnitOfWorkOptions options, CancellationToken ct = default)
    {
        if (options.IsTransactional)
            _tx = await _dbContext.Database.BeginTransactionAsync(ct);
        return new EfLocal(_dbContext, _tx);
    }

    private sealed class EfLocal : ILocalTransaction
    {
        private readonly DbContext _ctx;
        private readonly IDbContextTransaction? _tx;
        public EfLocal(DbContext ctx, IDbContextTransaction? tx) { _ctx = ctx; _tx = tx; }

        public async Task CommitAsync(CancellationToken ct = default)
        {
            await _ctx.SaveChangesAsync(ct);
            if (_tx != null) await _tx.CommitAsync(ct);
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            if (_tx != null) await _tx.RollbackAsync(ct);
        }
    }
}
```

---

## 7. PostSharp [UnitOfWork] Attribute
Provides declarative transaction management. Automatically begins a UoW if one does not exist and commits/rolls back appropriately.

```csharp
[PSerializable]
public sealed class UnitOfWorkAttribute : MethodInterceptionAspect
{
    public bool IsTransactional { get; set; } = true;
    public UnitOfWorkScopeOption Scope { get; set; } = UnitOfWorkScopeOption.Required;

    public override async Task OnInvokeAsync(MethodInterceptionArgs args)
    {
        var sp = AmbientServiceProvider.Current ?? AmbientServiceProvider.Root;
        var uow = sp.GetRequiredService<IUnitOfWorkManager>();
        var options = new UnitOfWorkOptions { IsTransactional = IsTransactional, Scope = Scope };

        await using var scope = await uow.BeginAsync(options);
        try
        {
            await args.ProceedAsync();
            await scope.CommitAsync();
        }
        catch
        {
            await scope.RollbackAsync();
            throw;
        }
    }
}
```

---

## 8. Dependency Injection Setup

```csharp
services.AddSingleton<IAmbientUnitOfWorkAccessor, AsyncLocalAmbientUowAccessor>();
services.AddScoped<IUnitOfWorkManager, UnitOfWorkManager>();
services.AddDbContext<MyAppDbContext>(o => o.UseNpgsql(cfg.GetConnectionString("Default")));
services.AddScoped<ILocalTransactionSource, EfCoreTransactionSource<MyAppDbContext>>();
services.AddScoped<ILocalTransactionSource, DapperTransactionSource<MyAppDbContext>>();
```

---

## 9. Benefits
- ✅ Full provider isolation with composite transaction orchestration.  
- ✅ Seamless ambient propagation across threads (AsyncLocal).  
- ✅ Eliminates ref-counting complexity using scope participation.  
- ✅ Declarative transaction handling via PostSharp attributes.  
- ✅ Consistent rollback and commit semantics across data sources.  
- ✅ Extensible to new storage providers (Redis, Kafka, EventStore).  
- ✅ Works across web requests, background workers, and CLI environments.

---

## 10. Summary
The finalized UoW implementation offers a robust and extensible foundation for multi-provider transactional control.  
By combining CompositeUnitOfWork, AsyncLocal ambient propagation, and PostSharp AOP, this architecture achieves isolation, simplicity, and declarative control suitable for enterprise-scale distributed systems.
