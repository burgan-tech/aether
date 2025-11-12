# Aether Framework

**Enterprise-grade .NET SDK for building scalable, cloud-native applications with Domain-Driven Design and distributed architecture patterns.**

[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## 🎯 Purpose

Aether Framework is a comprehensive SDK designed to accelerate enterprise application development by providing production-ready implementations of essential architectural patterns and cross-cutting concerns. Built with modern cloud-native principles, it eliminates boilerplate code and lets development teams focus on business logic.

### Key Benefits
- 🏗️ **Proven Architecture Patterns** - Repository, Unit of Work, DDD building blocks, CQRS-ready services
- 🌐 **Distributed-First** - Event bus, distributed cache/lock, inbox/outbox patterns
- 📊 **Built-in Observability** - OpenTelemetry integration with traces, metrics, and logs
- 🚀 **Cloud-Native Ready** - Dapr integration, containerization support
- 📦 **Modular Design** - Use only what you need
- 🎯 **Developer-Friendly** - Convention over configuration with intuitive APIs

## 📚 Documentation

Comprehensive documentation is available in the [`framework/docs`](framework/docs/) directory:

- **[Complete Framework Documentation](framework/docs/README.md)** - Feature overview, quick start guides, and best practices
- **[Framework README](framework/README.md)** - Architecture and project structure

### Quick Links by Category

**Core Infrastructure**
- [Repository Pattern](framework/docs/repository-pattern/) | [Unit of Work](framework/docs/unit-of-work/)

**Domain-Driven Design**
- [DDD Building Blocks](framework/docs/ddd/) | [Domain Events](framework/docs/domain-events/)

**Event Architecture**
- [Distributed Events](framework/docs/distributed-events/) | [Inbox & Outbox](framework/docs/inbox-outbox/)

**Application Layer**
- [Application Services](framework/docs/application-services/)

**Infrastructure Services**
- [Distributed Cache](framework/docs/distributed-cache/) | [Distributed Lock](framework/docs/distributed-lock/) | [Background Jobs](framework/docs/background-job/)

**Cross-Cutting Concerns**
- [Object Mapping](framework/docs/mapper/) | [GUID Generation](framework/docs/guid-generation/) | [OpenTelemetry](framework/docs/telemetry/) | [Response Compression](framework/docs/response-compression/) | [HTTP Client](framework/docs/http-client/)

## 🚀 Quick Start

```bash
# Install core packages
dotnet add package BBT.Aether.AspNetCore
dotnet add package BBT.Aether.Infrastructure

# Use CLI to scaffold a new project
dotnet run --project framework/src/BBT.Aether.Cli create MyProject -tm MyTeam -t api
```

**Minimal Setup Example:**

```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure Aether services
builder.Services.AddAetherCore(options => options.ApplicationName = "MyApp");
builder.Services.AddAetherInfrastructure();
builder.Services.AddAetherDbContext<MyDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddAetherEventBus(options => options.PubSubName = "pubsub");
builder.Services.AddAetherTelemetry(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseCorrelationId();
app.UseUnitOfWorkMiddleware();
app.Run();
```

See [framework documentation](framework/docs/README.md) for detailed examples and configuration options.

## 📦 Packages

| Package | Description | Dependencies |
|---------|-------------|--------------|
| `BBT.Aether.Core` | Core interfaces and abstractions | - |
| `BBT.Aether.Domain` | DDD building blocks and domain layer | Core |
| `BBT.Aether.Infrastructure` | Infrastructure implementations | Core, Domain |
| `BBT.Aether.Application` | Application service patterns | Infrastructure |
| `BBT.Aether.AspNetCore` | ASP.NET Core integrations | Infrastructure |
| `BBT.Aether.Aspects` | PostSharp aspect implementations | Core |
| `BBT.Aether.HttpClient` | HTTP client abstractions | Core |
| `BBT.Aether.TestBase` | Testing utilities | Core |

## 🏗️ Project Structure

```
aether/
├── framework/                    # Main framework solution
│   ├── src/                     # Source projects
│   │   ├── BBT.Aether.Core/
│   │   ├── BBT.Aether.Domain/
│   │   ├── BBT.Aether.Infrastructure/
│   │   ├── BBT.Aether.Application/
│   │   ├── BBT.Aether.AspNetCore/
│   │   ├── BBT.Aether.Aspects/
│   │   ├── BBT.Aether.HttpClient/
│   │   ├── BBT.Aether.TestBase/
│   │   └── BBT.Aether.Cli/
│   └── docs/                    # Comprehensive documentation
├── build/                       # Build scripts
└── README.md                    # This file
```

## 🤝 Contributing

We welcome contributions from the community! Here's how you can help:

### Getting Started
1. **Fork the repository** and clone it locally
2. **Create a feature branch** from `main`
   ```bash
   git checkout -b feature/your-feature-name
   ```
3. **Make your changes** following our coding standards
4. **Write tests** for new functionality
5. **Update documentation** as needed
6. **Submit a pull request** with a clear description

### Contribution Guidelines
- Follow existing code style and conventions
- Ensure all tests pass before submitting
- Add XML documentation comments for public APIs
- Update relevant documentation in `framework/docs/`
- Keep commits focused and write clear commit messages
- Reference related issues in your PR description

### Areas for Contribution
- 🐛 Bug fixes and issue resolution
- ✨ New feature implementations
- 📖 Documentation improvements
- 🧪 Test coverage enhancements
- 🌍 Localization and translations
- 💡 Feature suggestions and discussions

### Code of Conduct
- Be respectful and inclusive
- Provide constructive feedback
- Focus on what is best for the community
- Show empathy towards other community members

## 📄 License

This project is licensed under the **MIT License** - see the [LICENSE](LICENSE) file for details.

Copyright (c) 2025 Burgan Bank Technology

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED.

---

## 🔗 Resources

- **Documentation**: [framework/docs/README.md](framework/docs/README.md)
- **Issues**: [GitHub Issues](https://github.com/burgan-tech/aether/issues)
- **Discussions**: [GitHub Discussions](https://github.com/burgan-tech/aether/discussions)
- **NuGet Packages**: [Package Publishing Guide](framework/NuGet.md)

## 💬 Support

- 📧 For questions and support, open a [GitHub Discussion](https://github.com/burgan-tech/aether/discussions)
- 🐛 For bug reports, create a [GitHub Issue](https://github.com/burgan-tech/aether/issues)
- 📖 For detailed usage, see the [comprehensive documentation](framework/docs/README.md)

---

**Built with ❤️ by Burgan Bank Technology Team**

