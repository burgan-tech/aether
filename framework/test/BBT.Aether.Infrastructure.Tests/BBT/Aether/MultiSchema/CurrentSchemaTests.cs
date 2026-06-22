using System.Linq;
using System.Threading.Tasks;
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

    [Fact]
    public async Task Change_is_isolated_across_parallel_async_flows()
    {
        var sut = New();

        using (sut.Change("base"))
        {
            var tasks = Enumerable.Range(0, 32).Select(async i =>
            {
                var schema = $"s{i}";
                using (sut.Change(schema))
                {
                    await Task.Yield();
                    sut.Name.ShouldBe(schema);
                    await Task.Delay(1);
                    sut.Name.ShouldBe(schema);
                }
            });

            await Task.WhenAll(tasks); // must NOT throw "Schema scope corrupted"
            sut.Name.ShouldBe("base");
        }

        sut.Name.ShouldBeNull();
    }

    private sealed class PassthroughFormatter : ISchemaNameFormatter
    {
        public string Format(string schema) => schema;
    }
}
