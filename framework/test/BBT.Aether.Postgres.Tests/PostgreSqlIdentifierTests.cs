using BBT.Aether.MultiSchema;
using Shouldly;
using Xunit;

namespace BBT.Aether.Postgres.Tests;

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

    [Fact]
    public void QuoteSchema_rejects_identifiers_longer_than_63_bytes()
    {
        var tooLong = new string('a', 64);
        Should.Throw<System.ArgumentException>(() => PostgreSqlIdentifier.QuoteSchema(tooLong));
    }

    [Fact]
    public void QuoteSchema_accepts_identifier_at_the_63_byte_limit()
    {
        var atLimit = new string('a', 63);
        PostgreSqlIdentifier.QuoteSchema(atLimit).ShouldBe($"\"{atLimit}\"");
    }
}
