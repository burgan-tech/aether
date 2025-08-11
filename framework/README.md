# Aether Framework

Aether is a .NET Core framework designed to provide developers with a foundational structure and pre-built cross-cutting concerns. It aims to accelerate development by offering ready-to-use components and patterns.

## Project Structure

The solution is organized into several projects:

-   **BBT.Aether.Application**: Contains the application logic and business workflows. It depends on the [BBT.Aether.Domain](src/BBT.Aether.Domain/BBT.Aether.Domain.csproj) and [BBT.Aether.Core](src/BBT.Aether.Core/BBT.Aether.Core.csproj) projects.
-   **BBT.Aether.AspNetCore**: Provides extensions and utilities for building ASP.NET Core applications. It depends on the [BBT.Aether.Application](src/BBT.Aether.Application/BBT.Aether.Application.csproj), [BBT.Aether.Domain](src/BBT.Aether.Domain/BBT.Aether.Domain.csproj), and [BBT.Aether.Core](src/BBT.Aether.Core/BBT.Aether.Core.csproj) projects.
-   **BBT.Aether.Core**: Contains core functionalities, extensions, and base classes used throughout the framework.
-   **BBT.Aether.Domain**: Defines the domain entities, interfaces, and business rules. It depends on the [BBT.Aether.Core](src/BBT.Aether.Core/BBT.Aether.Core.csproj) project.
-   **BBT.Aether.Infrastructure**: Implements the infrastructure concerns such as data access, logging, and caching. It depends on the [BBT.Aether.Domain](src/BBT.Aether.Domain/BBT.Aether.Domain.csproj) project.
-   **BBT.Aether.TestBase**: Provides a base class for creating integration and unit tests. It depends on the [BBT.Aether.Core](src/BBT.Aether.Core/BBT.Aether.Core.csproj) project.

## Dependencies

The framework utilizes several NuGet packages, including:

-   JetBrains.Annotations
-   Microsoft.AspNetCore.OpenApi
-   Microsoft.EntityFrameworkCore
-   Microsoft.Extensions.Configuration.CommandLine
-   Microsoft.SourceLink.GitHub

## Target Frameworks

The projects target multiple .NET frameworks:

-   net9.0
-   net8.0
-   netstandard2.0
-   netstandard2.1

## Getting Started

1.  Clone the repository.
2.  Open the `BBT.Aether.sln` solution file.
3.  Build the solution.
4.  Explore the various projects and their functionalities.

## Usage

To use the Aether framework in your projects, reference the appropriate Aether projects based on your needs.

## Cli

`dotnet run BBT.Aether.Cli create PROJECT_NAME -tm TEAM_NAME -t api -o PATH`

## Contributing

Contributions are welcome! Please follow the contribution guidelines.