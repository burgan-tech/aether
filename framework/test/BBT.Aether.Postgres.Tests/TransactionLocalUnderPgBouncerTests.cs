using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

/// <summary>
/// F0 doğruluk kapısı: PostgreSQL'in ÖNÜNDE <b>pgbouncer (transaction pool)</b> ve
/// <see cref="SchemaSwitchingMode.TransactionLocal"/> ile, per-operation transactional modelin
/// temel özelliğini kanıtlar:
/// <list type="number">
/// <item>Non-transactional bir unit-of-work TransactionLocal altında ilk komutta patlar
/// ("requires a transaction, but none is active") — mevcut non-transactional pipeline'ın
/// preprod'da neden kırıldığının belgelenmiş kanıtı.</item>
/// <item>KISA transactional bir unit-of-work aynı ortamda (pgbouncer transaction pool +
/// TransactionLocal) BAŞARIR — refactor'ün hedeflediği desen.</item>
/// <item>Ardışık kısa transactional birimler, farklı flow şemalarını (transaction pooling connection
/// multiplex etse bile) doğru çözer — per-op transaction + transaction pooling şema yönlendirmesi.</item>
/// </list>
/// <para>
/// ÇALIŞTIRMA NOTU: pgbouncer imajı (edoburu/pgbouncer, docker.io) gerektirir; kısıtlı/registry-bloke
/// ortamlarda çekilemez → CI/preprod'da koşun. Container auth/imaj yapılandırması ortamınıza göre
/// ayar gerektirebilir; testin DEĞERİ assertion'lardadır, tam container reçetesi değil.
/// </para>
/// </summary>
public sealed class TransactionLocalUnderPgBouncerTests : IAsyncLifetime
{
    private const string Db = "testdb";
    private const string User = "test";
    private const string Pass = "test";
    private const string PgAlias = "postgres-upstream";

    private INetwork _network = null!;
    private PostgreSqlContainer _postgres = null!;
    private IContainer _pgbouncer = null!;

    private string _directConnStr = null!;   // doğrudan postgres — sadece DDL kurulumu için
    private string _pgbouncerConnStr = null!; // pgbouncer transaction pool — testlerin bağlantısı

    async Task IAsyncLifetime.InitializeAsync()
    {
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases(PgAlias)
            .WithUsername(User)
            .WithPassword(Pass)
            .WithDatabase(Db)
            .Build();
        await _postgres.StartAsync();
        _directConnStr = _postgres.GetConnectionString();

        // pgbouncer, upstream postgres'e ağ aliası üzerinden ulaşır; transaction pool modunda dinler.
        _pgbouncer = new ContainerBuilder()
            .WithImage("edoburu/pgbouncer:latest")
            .WithNetwork(_network)
            .WithEnvironment("DB_HOST", PgAlias)
            .WithEnvironment("DB_PORT", "5432")
            .WithEnvironment("DB_USER", User)
            .WithEnvironment("DB_PASSWORD", Pass)
            .WithEnvironment("DB_NAME", Db)
            .WithEnvironment("POOL_MODE", "transaction")          // <-- kritik: transaction pooling
            .WithEnvironment("AUTH_TYPE", "scram-sha-256")        // postgres 16 varsayılanı
            .WithEnvironment("MAX_CLIENT_CONN", "100")
            .WithEnvironment("DEFAULT_POOL_SIZE", "20")
            .WithEnvironment("LISTEN_PORT", "6432")
            .WithPortBinding(6432, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(6432))
            .Build();
        await _pgbouncer.StartAsync();

        _pgbouncerConnStr =
            $"Host={_pgbouncer.Hostname};Port={_pgbouncer.GetMappedPublicPort(6432)};" +
            $"Username={User};Password={Pass};Database={Db};Pooling=true;Max Auto Prepare=0";

        // Şemaları + tabloları doğrudan postgres bağlantısı üzerinden kur (model = EF create script).
        await ArrangeSchemaAsync("flow_x");
        await ArrangeSchemaAsync("flow_y");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_pgbouncer is not null) await _pgbouncer.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
        if (_network is not null) await _network.DeleteAsync();
    }

    // ---- Entity + DbContext ---------------------------------------------------------------

    private sealed class Order
    {
        public Guid Id { get; set; }
        public string Customer { get; set; } = string.Empty;
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : AetherDbContext<TestDbContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Order>(e =>
            {
                e.ToTable("orders"); // şema YOK — çalışma anında search_path ile çözülür
                e.HasKey(o => o.Id);
                e.Property(o => o.Customer).IsRequired();
            });
        }
    }

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddAetherCore(_ => { });
        // pgbouncer transaction pool + TransactionLocal — preprod/prod ile aynı kombinasyon.
        services.AddAetherNpgsql<TestDbContext>(_pgbouncerConnStr, SchemaSwitchingMode.TransactionLocal);
        return services.BuildServiceProvider();
    }

    private async Task ArrangeSchemaAsync(string schema)
    {
        await using (var conn = new NpgsqlConnection(_directConnStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{schema}\";";
            await cmd.ExecuteNonQueryAsync();
        }

        // Model → create script (tablo niteliksiz), hedef şemanın search_path'inde çalıştır.
        var builder = new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(_directConnStr);
        await using var ctx = new TestDbContext(builder.Options);
        var script = ctx.Database.GenerateCreateScript();

        await using (var ddl = new NpgsqlConnection(_directConnStr))
        {
            await ddl.OpenAsync();
            await using (var setCmd = ddl.CreateCommand())
            {
                setCmd.CommandText = $"SET search_path TO \"{schema}\";";
                await setCmd.ExecuteNonQueryAsync();
            }
            await using (var ddlCmd = ddl.CreateCommand())
            {
                ddlCmd.CommandText = script;
                await ddlCmd.ExecuteNonQueryAsync();
            }
        }
    }

    private async Task<long> CountAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(_directConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{schema}\".\"orders\"";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // ---- Tests ----------------------------------------------------------------------------

    [Fact]
    public async Task NonTransactionalUoW_UnderTransactionLocal_ThrowsRequiresTransaction()
    {
        var sp = BuildProvider();
        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        using (currentSchema.Change("flow_x"))
        {
            // Non-transactional (IsTransactional=false) — mevcut pipeline'ın UoW tipi.
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = false });

            var ex = await Should.ThrowAsync<Exception>(async () =>
            {
                var ctx = await provider.GetDbContextAsync();
                _ = await ctx.Set<Order>().AnyAsync();
            });

            // Zincirin herhangi bir yerinde SearchPathCommandInterceptor'ın mesajı olmalı.
            (ex.ToString()).ShouldContain("requires a transaction");
        }
    }

    [Fact]
    public async Task TransactionalUoW_UnderTransactionLocal_OnPgBouncerTransactionPool_Succeeds()
    {
        var sp = BuildProvider();
        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        using (currentSchema.Change("flow_x"))
        {
            // Kısa transactional birim — hedef desen. SET LOCAL search_path transaction içinde çalışır,
            // pgbouncer transaction pool connection'ı transaction boyunca pinler.
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            var ctx = await provider.GetDbContextAsync();
            ctx.Set<Order>().Add(new Order { Id = Guid.NewGuid(), Customer = "Alice" });
            await uow.SaveChangesAsync();

            var countInTx = await ctx.Set<Order>().CountAsync(); // aynı transaction içinde read
            countInTx.ShouldBe(1);

            await uow.CommitAsync();
        }

        (await CountAsync("flow_x")).ShouldBe(1);
    }

    [Fact]
    public async Task ShortTransactions_ForDifferentSchemas_RouteToCorrectSchema()
    {
        var sp = BuildProvider();

        // İki ayrı kısa transactional birim, iki ayrı flow şeması. Transaction pooling connection'ı
        // multiplex etse de, her birim SET LOCAL ile kendi şemasını çözmeli.
        await InsertInScopeAsync(sp, "flow_x", "X-customer");
        await InsertInScopeAsync(sp, "flow_y", "Y-customer");

        (await CountAsync("flow_x")).ShouldBe(1);
        (await CountAsync("flow_y")).ShouldBe(1);
    }

    private static async Task InsertInScopeAsync(IServiceProvider sp, string schema, string customer)
    {
        await using var scope = sp.CreateAsyncScope();
        var ssp = scope.ServiceProvider;
        var currentSchema = ssp.GetRequiredService<ICurrentSchema>();
        var uowManager = ssp.GetRequiredService<IUnitOfWorkManager>();
        var provider = ssp.GetRequiredService<IAetherDbContextProvider<TestDbContext>>();

        using (currentSchema.Change(schema))
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            var ctx = await provider.GetDbContextAsync();
            ctx.Set<Order>().Add(new Order { Id = Guid.NewGuid(), Customer = customer });
            await uow.SaveChangesAsync();
            await uow.CommitAsync();
        }
    }
}
