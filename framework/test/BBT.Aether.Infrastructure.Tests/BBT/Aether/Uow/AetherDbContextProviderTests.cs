using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.Uow;

public sealed class AetherDbContextProviderTests
{
    private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> o) : DbContext(o);

    [Fact]
    public async Task Throws_when_no_schema()
    {
        var cs = Substitute.For<ICurrentSchema>();
        cs.Name.Returns((string?)null);
        var mgr = Substitute.For<IUnitOfWorkManager>();
        var sut = new AetherDbContextProvider<ProbeDbContext>(cs, mgr);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => sut.GetDbContextAsync());
        ex.Message.ShouldContain("Current schema is not set");
    }

    [Fact]
    public async Task Throws_when_no_uow()
    {
        var cs = Substitute.For<ICurrentSchema>();
        cs.Name.Returns("flow_a");
        var mgr = Substitute.For<IUnitOfWorkManager>();
        mgr.Current.Returns((IUnitOfWork?)null);
        var sut = new AetherDbContextProvider<ProbeDbContext>(cs, mgr);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => sut.GetDbContextAsync());
        ex.Message.ShouldContain("No active UnitOfWork");
    }

    [Fact]
    public async Task Delegates_to_uow_with_resolved_schema()
    {
        var cs = Substitute.For<ICurrentSchema>();
        cs.Name.Returns("flow_a");
        var efUow = Substitute.For<IEfCoreUnitOfWork>();
        var ctx = new ProbeDbContext(
            new DbContextOptionsBuilder<ProbeDbContext>().UseInMemoryDatabase("x").Options);
        efUow.GetDbContextAsync<ProbeDbContext>("flow_a", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ctx));
        var mgr = Substitute.For<IUnitOfWorkManager>();
        mgr.Current.Returns(efUow);
        var sut = new AetherDbContextProvider<ProbeDbContext>(cs, mgr);

        (await sut.GetDbContextAsync()).ShouldBeSameAs(ctx);
        await efUow.Received(1).GetDbContextAsync<ProbeDbContext>("flow_a", Arg.Any<CancellationToken>());
    }
}
