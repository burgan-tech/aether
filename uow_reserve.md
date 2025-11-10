Kesinlikle olur, hatta tam ABP’nin de yaptığı şeye yaklaşıyorsun 👌
“UoW lifecycle hook” API’si hem Outbox/Domain Event pipeline’ını temiz taşımamızı sağlar hem de geliştiricinin “şu iş sadece UoW başarıyla bittiyse/çöktüyse/bitince çalışsın” ihtiyacını şık şekilde karşılar.

Bunu doğru tasarlarsak:
	•	UoW iç davranışı bozulmaz,
	•	Nested/Child UoW ile uyumlu olur,
	•	Geliştirici kendi işini bu hook’lara asar, attribute spam yok,
	•	Outbox/DistributedEvent gibi şeyleri de aynı mekanizma ile yönetebilirsin.

Aşağıya doğrudan kullanabileceğin tasarım + implementasyon koyuyorum.

⸻

1. Hedef API (kullanıcı gözünden)

Geliştirici şunu yapabilsin:

public class MyService
{
    private readonly IUnitOfWorkManager _uowManager;

    public MyService(IUnitOfWorkManager uowManager)
    {
        _uowManager = uowManager;
    }

    public async Task HandleAsync()
    {
        await using var uow = await _uowManager.BeginAsync(new()
        {
            IsTransactional = true,
            Scope = UnitOfWorkScopeOption.Required
        });

        uow.OnCompleted(async () =>
        {
            // UoW başarıyla commit edildiğinde
            // cache invalidate, search index, log vs
        });

        uow.OnFailed(async () =>
        {
            // Commit başarısız / rollback oldu
            // retry schedule, compensating action, telemetry vs
        });

        uow.OnDisposed(() =>
        {
            // Her durumda çalışır (cleanup)
        });

        // business + EF operations...

        await uow.CommitAsync();
    }
}

Ve:
	•	ChildUnitOfWork kullanan kodlar bu hook’ları root UoW’a attach eder (yani tek event noktası).
	•	NullUnitOfWork bunları no-op yapar (Suppress senaryosu).
	•	Middleware’deki reserved UoW da (IsReserved) olayları normal UoW gibi expose edebilir ama:
	•	Initialize edilmezse OnCompleted fiilen tetiklenmez ⇒ no-op.

⸻

2. Interface Tasarımı

IUnitOfWork’a event/delegate based hook’lar ekleyelim, ama klasik event yerine “IDisposable dönen subscription” pattern’i öneriyorum. Sebep:
Kolay unsubscribe, memory leak riskini azaltır, ABP de buna benzer yapıyor.

public interface IUnitOfWork : IAsyncDisposable
{
    Guid Id { get; }

    UnitOfWorkOptions? Options { get; }
    IUnitOfWork? Outer { get; }

    bool IsReserved { get; }
    string? ReservationName { get; }

    bool IsCompleted { get; }
    bool IsDisposed { get; }

    void Reserve(string reservationName);
    void Initialize(UnitOfWorkOptions options);
    bool IsReservedFor(string reservationName);
    void SetOuter(IUnitOfWork? outer);

    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);

    // 🔽 Lifecycle hooks
    IDisposable OnCompleted(Func<Task> handler);
    IDisposable OnFailed(Func<Task> handler);
    IDisposable OnDisposed(Action handler); // sync yeterli, istersen Func<Task> da yaparsın
}


⸻

3. Concrete UnitOfWork Implementasyonu

Basit ve net tutalım:

public sealed class UnitOfWork : IUnitOfWork
{
    public Guid Id { get; } = Guid.NewGuid();
    public UnitOfWorkOptions? Options { get; private set; }
    public IUnitOfWork? Outer { get; private set; }
    public bool IsReserved { get; private set; }
    public string? ReservationName { get; private set; }
    public bool IsCompleted { get; private set; }
    public bool IsDisposed { get; private set; }

    private readonly List<Func<Task>> _completedHandlers = new();
    private readonly List<Func<Task>> _failedHandlers = new();
    private readonly List<Action> _disposedHandlers = new();

    // ctor'da DbContext / TransactionSource / Logger vs inject edilir

    public void Reserve(string reservationName)
    {
        if (Options is not null)
            throw new InvalidOperationException("Already initialized; cannot reserve.");

        ReservationName = reservationName;
        IsReserved = true;
    }

    public bool IsReservedFor(string reservationName)
        => IsReserved && string.Equals(ReservationName, reservationName, StringComparison.Ordinal);

    public void Initialize(UnitOfWorkOptions options)
    {
        if (Options is not null)
            throw new InvalidOperationException("UoW already initialized.");

        Options = options;
        IsReserved = false;

        // burada IsTransactional == true ise transaction başlatabilirsin
        // veya lazy-transactions kullanıyorsan ileride başlatırsın
    }

    public void SetOuter(IUnitOfWork? outer) => Outer = outer;

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (IsReserved)
        {
            // ABP style: reserved commit => no-op
            IsCompleted = true;
            return;
        }

        if (IsCompleted) return;

        try
        {
            // 1) Domain events → envelopes
            // 2) Outbox mesj ekle
            // 3) SaveChanges
            // 4) Transaction commit

            IsCompleted = true;

            // 5) OnCompleted hook'ları çağır
            await InvokeCompletedHandlersAsync();
        }
        catch
        {
            await InvokeFailedHandlersAsync();
            throw;
        }
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (IsReserved || IsCompleted) return;

        try
        {
            // Transaction rollback
        }
        finally
        {
            IsCompleted = true;
            await InvokeFailedHandlersAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsDisposed)
            return;

        // Eğer commit/rollback yapılmadıysa: safety rollback
        if (!IsCompleted && !IsReserved)
        {
            await RollbackAsync();
        }

        // Dispose hook'ları her durumda
        InvokeDisposedHandlers();

        IsDisposed = true;
    }

    // Lifecycle registration

    public IDisposable OnCompleted(Func<Task> handler)
    {
        _completedHandlers.Add(handler);
        return new Subscription(_completedHandlers, handler);
    }

    public IDisposable OnFailed(Func<Task> handler)
    {
        _failedHandlers.Add(handler);
        return new Subscription(_failedHandlers, handler);
    }

    public IDisposable OnDisposed(Action handler)
    {
        _disposedHandlers.Add(handler);
        return new Subscription(_disposedHandlers, handler);
    }

    private async Task InvokeCompletedHandlersAsync()
    {
        foreach (var h in _completedHandlers.ToArray())
        {
            try { await h(); }
            catch { /* log et; commit'i geri almaya çalışma */ }
        }
    }

    private async Task InvokeFailedHandlersAsync()
    {
        foreach (var h in _failedHandlers.ToArray())
        {
            try { await h(); }
            catch { /* log et */ }
        }
    }

    private void InvokeDisposedHandlers()
    {
        foreach (var h in _disposedHandlers.ToArray())
        {
            try { h(); }
            catch { /* log et */ }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly IList<object> _list;
        private readonly object _handler;
        private bool _disposed;

        public Subscription(IList<object> list, object handler)
        {
            _list = list;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _list.Remove(_handler);
        }
    }
}

Önemli semantic kararlar
	•	OnCompleted callback hata atarsa commit’i geri almıyoruz.
UoW zaten commit etmiş durumda. Bu hook “post-commit reaction”.
	•	OnFailed rollback sonrası çağrılır; burada retry/notification planlayabilirsin.
	•	OnDisposed her durumda çalışır: success, fail, reserved (istersen reserved hariç tutabilirsin).

⸻

4. ChildUnitOfWork ile Uyumluluk

Child UoW, hook’ları parent’a attach etmeli ki:

internal sealed class ChildUnitOfWork : IUnitOfWork
{
    private readonly IUnitOfWork _parent;

    // ... diğer delegasyonlar

    public IDisposable OnCompleted(Func<Task> handler) => _parent.OnCompleted(handler);
    public IDisposable OnFailed(Func<Task> handler) => _parent.OnFailed(handler);
    public IDisposable OnDisposed(Action handler) => _parent.OnDisposed(handler);

    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

Böylece:
	•	[UnitOfWork] ile açılan üst seviye scope, event’leri tek noktadan yönetir.
	•	İç nested scope’lar aynı pipeline’a ek event ekleyip çıkarabilir.

⸻

5. NullUnitOfWork (Suppress senaryosu)

public sealed class NullUnitOfWork : IUnitOfWork
{
    // tüm property/methodlar no-op
    public IDisposable OnCompleted(Func<Task> handler) => Disposable.Empty;
    public IDisposable OnFailed(Func<Task> handler) => Disposable.Empty;
    public IDisposable OnDisposed(Action handler) => Disposable.Empty;
}

Böylece Suppress kullanan yerler de aynı API’yi kullanabilir; event’ler ama çalışmaz.

⸻

6. Neyi Çözüyorsun?

Bu hook tasarımı ile:
	1.	Outbox/DomainEvent:
	•	OnCompleted içinde event dispatch (veya queue push) tetikleyebilirsin.
	•	UoW commit-success garantisine bağlanmış olur.
	2.	Distributed Event sonrası iş:
	•	OnFailed ile compensating logic (örn. saga orchestration) başlatılabilir.
	3.	Uzantı Noktaları:
	•	Farklı bounded context’ler, framework kodunu modifiye etmeden UoW lifecycle’a abone olabilir.
	4.	ABP ile zihinsel uyum:
	•	ABP’nin OnCompleted, OnFailed, OnDisposed yapısıyla aynı yaklaşım.
	5.	Loop / karışıklık yok:
	•	Hook’lar UoW içinde; Store sınıfları hala SaveChanges çağırmıyor.
	•	UoW commit sırası belli: domain events + outbox + save + commit + completed hooks.

⸻

7. Son Not: “Bu public mi olsun, internal mi?”

Benim önerim:
	•	IUnitOfWork.OnCompleted/OnFailed/OnDisposed public exposed olsun.
	•	Ama dokümantasyonla net yaz:
	•	“Bu hook’lar cross-cutting ve integration amaçlıdır; domain invariant’larını burada değiştirmeyin.”
	•	İç implementasyon Outbox/DomainEvent için de aynı mekanizmayı kullanabilir; duplication yok.
