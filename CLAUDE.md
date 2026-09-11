# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

### Build
```bash
# Build the framework solution
dotnet build framework/BBT.Aether.slnx

# Release build
dotnet build framework/BBT.Aether.slnx --configuration Release

# PowerShell build script (from repo root)
.\build\build-all.ps1
```

### Individual Project Build
```bash
dotnet build framework/src/BBT.Aether.Infrastructure
```

### CLI Tool (Project Scaffolding)
```bash
dotnet run --project framework/src/BBT.Aether.Cli create PROJECT_NAME -tm TEAM_NAME -t api -o OUTPUT_PATH
```

### NuGet Packaging
Packages are published automatically via GitHub Actions on `release-v*` branches. Version is auto-calculated from the branch name (e.g., `release-v1.0` → `1.0.0`, `1.0.1`, ...).

#### Local feed for unreleased work (consumed by vnext)

```bash
./build/pack-local.sh              # packs every framework/src library as 1.0.NN-local into .local-feed/
./build/pack-local.sh 1.0.41-local # explicit version; must end in -local (the script refuses otherwise)
```

- `.local-feed/` is git-ignored; `-local` versions can never shadow a published release.
- The script purges `~/.nuget/packages/bbt.aether.*/<version>` before packing — without that, re-packing
  the same version silently keeps the old contents in the build ("the member I just added does not exist").
- Consumer side (vnext): uncomment **both** the `aether-local` source (`../aether/.local-feed`) and its
  `packageSourceMapping` block in `nuget.config`, then set `<AetherPackageVersion>` in
  `Directory.Build.props` to the packed version. Revert both before opening a vnext PR — CI cannot restore
  a `-local` version. Full procedure: vnext `docs/testing/integration-testing.md` §8.

## Architecture

Aether is a .NET 10 SDK/framework targeting enterprise cloud-native applications, published as 13 NuGet packages. The solution file is `framework/BBT.Aether.slnx` (modern `.slnx` format).

### Layer Dependencies

```
BBT.Aether.Core           ← no framework dependencies
BBT.Aether.Abstractions   ← no framework dependencies
BBT.Aether.Domain         ← Core
BBT.Aether.Infrastructure ← Domain, Abstractions (+ EF Core, Dapr, Redis) — provider-agnostic (no specific DB provider; comes from BBT.Aether.Npgsql/BBT.Aether.SqlServer)
BBT.Aether.Npgsql         ← Infrastructure (+ Npgsql.EntityFrameworkCore.PostgreSQL) — PostgreSQL provider (full multi-schema)
BBT.Aether.SqlServer      ← Infrastructure (+ Microsoft.EntityFrameworkCore.SqlServer) — SQL Server provider (single-schema)
BBT.Aether.Mapperly       ← Core (+ Riok.Mapperly source generator) — default mapper
BBT.Aether.AutoMapper     ← Core (+ AutoMapper) — opt-in, requires commercial license
BBT.Aether.Application    ← Domain, Infrastructure
BBT.Aether.AspNetCore     ← Application, Infrastructure (+ ASP.NET Core middleware)
BBT.Aether.Aspects        ← PostSharp AOP cross-cutting aspects
BBT.Aether.HttpClient     ← Core (typed HTTP client abstractions)
BBT.Aether.TestBase       ← base classes for integration/unit tests
BBT.Aether.Cli            ← project scaffolding tool
```

### Key Patterns & Where They Live

- **DDD building blocks** (Entity, AggregateRoot, ValueObject, AuditedAggregateRoot): `BBT.Aether.Domain`
- **Repository & Unit of Work** (EF Core, multi-provider transactions): `BBT.Aether.Infrastructure`
- **Distributed Events** (CloudEvents + Dapr pub/sub): `BBT.Aether.Abstractions` (contracts), `BBT.Aether.Infrastructure` (impl)
- **Inbox/Outbox** (transactional messaging): `BBT.Aether.Abstractions` + `BBT.Aether.Infrastructure`
- **Object Mapping** (IObjectMapper, MapperBase, TwoWayMapperBase, lifecycle hooks): `BBT.Aether.Core` (interfaces), `BBT.Aether.Mapperly` (default), `BBT.Aether.AutoMapper` (opt-in)
- **Application services** (CRUD/ReadOnly base classes): `BBT.Aether.Application`
- **Middleware** (CorrelationId, CurrentUser, UnitOfWork, request/response logging): `BBT.Aether.AspNetCore`
- **Telemetry** (OpenTelemetry traces/metrics/logs with HTTP body logging): `BBT.Aether.AspNetCore`
- **Distributed Cache/Lock** (Redis + Dapr providers): `BBT.Aether.Infrastructure`
- **Background Jobs** (Dapr Jobs + Hangfire): `BBT.Aether.Infrastructure`

### Central Package Management

All NuGet versions are pinned in `Directory.Packages.props` at the repo root. When adding a new `<PackageReference>`, omit the `Version` attribute — it must be declared in `Directory.Packages.props` first.

### Project-Wide Settings

`common.props` is imported by every `.csproj`. It sets `LangVersion: latest`, nullable enforcement (`<Nullable>enable</Nullable>`, `<WarningsAsErrors>Nullable`), LGPL-3.0-only license metadata, and XML doc generation (`<GenerateDocumentationFile>true`).

### Suppressed Warnings

`common.props` suppresses `CS1591` (missing XML doc comments), `CS0436`, `DAPR_JOBS`, and `DAPR_DISTRIBUTEDLOCK`. Do not add `#pragma warning disable CS1591` in source files.

## Documentation

Feature documentation is in `framework/docs/` with a dedicated subfolder per concern (e.g., `docs/ddd/`, `docs/telemetry/`, `docs/inbox-outbox/`). Refer to these before implementing or modifying a feature area.
