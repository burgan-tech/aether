using System;
using BBT.Aether.MultiSchema;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.MultiSchema;

public sealed class StaticCurrentSchemaTests
{
    [Fact]
    public void Name_is_null_by_default()
        => new StaticCurrentSchema().Name.ShouldBeNull();

    [Fact]
    public void Name_returns_the_seeded_schema()
        => new StaticCurrentSchema("tenant_a").Name.ShouldBe("tenant_a");

    [Fact]
    public void Change_repoints_and_restores_on_dispose()
    {
        var cs = new StaticCurrentSchema("base");

        using (cs.Change("tenant_a"))
        {
            cs.Name.ShouldBe("tenant_a");

            using (cs.Change("tenant_b"))
            {
                cs.Name.ShouldBe("tenant_b");
            }

            cs.Name.ShouldBe("tenant_a"); // inner scope restored
        }

        cs.Name.ShouldBe("base"); // outer scope restored
    }

    [Fact]
    public void Change_from_null_restores_to_null()
    {
        var cs = new StaticCurrentSchema();

        using (cs.Change("tenant_a"))
        {
            cs.Name.ShouldBe("tenant_a");
        }

        cs.Name.ShouldBeNull();
    }

    [Fact]
    public void Change_rejects_empty_schema()
        => Should.Throw<ArgumentException>(() => new StaticCurrentSchema().Change("  "));

    [Fact]
    public void Dispose_is_idempotent()
    {
        var cs = new StaticCurrentSchema("base");
        var scope = cs.Change("tenant_a");

        scope.Dispose();
        cs.Name.ShouldBe("base");

        scope.Dispose(); // second dispose must be a no-op
        cs.Name.ShouldBe("base");
    }
}
