using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Aether.Uow.EntityFrameworkCore;

public sealed class AetherSchemaModelOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public DbContextOptionsExtensionInfo Info =>
        _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using AetherQualifiedNamesModel ";

        public override int GetServiceProviderHashCode() => 0xA37;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["Aether:QualifiedNamesModel"] = "1";

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo;
    }
}

public static class AetherSchemaModelOptionsBuilderExtensions
{
    public static DbContextOptionsBuilder UseAetherQualifiedNamesModel(
        this DbContextOptionsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ((IDbContextOptionsBuilderInfrastructure)builder)
            .AddOrUpdateExtension(new AetherSchemaModelOptionsExtension());
        return builder;
    }
}
