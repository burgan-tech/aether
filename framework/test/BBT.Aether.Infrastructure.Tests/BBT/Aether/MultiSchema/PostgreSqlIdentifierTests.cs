using BBT.Aether.MultiSchema;
using Shouldly;
using Xunit;

namespace BBT.Aether.Infrastructure.Tests.BBT.Aether.MultiSchema;

public sealed class PostgreSqlIdentifierTests
{
    [Theory]
    [InlineData("flow_kyc")]
    [InlineData("runtime_loan")]
    [InlineData("_audit")]
    public void QuoteSchema_allows_valid_identifiers(string schema)
        => PostgreSqlIdentifier.QuoteSchema(schema).ShouldBe($"\"{schema}\"");

    [Theory]
    [InlineData("flow kyc")]
    [InlineData("1flow")]
    [InlineData("flow;drop")]
    [InlineData("")]
    public void QuoteSchema_rejects_invalid_identifiers(string schema)
        => Should.Throw<System.InvalidOperationException>(() => PostgreSqlIdentifier.QuoteSchema(schema));
}
