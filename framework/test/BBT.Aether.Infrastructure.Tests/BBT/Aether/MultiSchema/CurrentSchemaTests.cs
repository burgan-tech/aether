using BBT.Aether.MultiSchema;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.MultiSchema;

public sealed class CurrentSchemaTests
{
    private static ICurrentSchema New() => new CurrentSchema(new PassthroughFormatter());

    [Fact]
    public void Name_is_null_when_no_scope()
        => New().Name.ShouldBeNull();

    [Fact]
    public void Change_sets_and_restores()
    {
        var cs = New();
        using (cs.Change("flow_a"))
        {
            cs.Name.ShouldBe("flow_a");
            using (cs.Change("flow_b"))
            {
                cs.Name.ShouldBe("flow_b");
            }
            cs.Name.ShouldBe("flow_a");
        }
        cs.Name.ShouldBeNull();
    }

    [Fact]
    public void Change_rejects_empty()
        => Should.Throw<System.ArgumentException>(() => New().Change(" "));

    private sealed class PassthroughFormatter : ISchemaNameFormatter
    {
        public string Format(string schema) => schema;
    }
}
