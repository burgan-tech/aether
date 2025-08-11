# GitHub Packages Publishing Setup

This document describes the automated NuGet package publishing setup for the BBT Aether Framework using GitHub Packages as the package repository.

## Overview

The project is configured to automatically build and publish NuGet packages to **GitHub Packages** when commits are pushed to branches matching the pattern `release-v*` (e.g., `release-v1.0`, `release-v2.5`).

## GitHub Actions Workflow

The workflow file `.github/workflows/nuget-publish.yml` handles:

- ✅ Building all framework and module projects
- ✅ Running tests
- ✅ Creating NuGet packages
- ✅ Publishing to GitHub Packages
- ✅ Creating GitHub releases with proper versioning
- ✅ Uploading packages as build artifacts

## Required Permissions & Setup

GitHub Packages publishing is automatically configured and requires minimal setup:

### 1. GITHUB_TOKEN

**Required**: Automatically provided by GitHub Actions  
**Description**: Used for publishing packages to GitHub Packages and creating GitHub releases  
**Setup**: No manual setup required - GitHub provides this automatically with the necessary permissions

### 2. Workflow Permissions

The workflow is configured with these permissions:
- `contents: write` - For creating releases and tags
- `packages: write` - For publishing packages to GitHub Packages

### 3. Repository Settings

Ensure GitHub Packages is enabled:
1. Go to your repository on GitHub
2. Navigate to Settings → General
3. Scroll to "Features" section
4. Ensure "Packages" is enabled

## Triggering the Workflow

### Automatic Trigger
The workflow automatically runs when:
- Code is pushed to any branch matching `release-v*`
- Examples: `release-v2.0.0`, `release-v2.1.0-beta.1`, `release-v3.0.0-rc.2`

### Manual Trigger
You can also trigger the workflow manually:
1. Go to Actions tab in your GitHub repository
2. Select "Build and Publish NuGet Packages" workflow
3. Click "Run workflow"
4. Choose the branch and optionally enable "Force publish packages"

## Version Management

The version is **automatically calculated from the branch name** with intelligent auto-incrementing:

### Branch-Based Versioning
- **Branch pattern**: `release-vX.Y` (e.g., `release-v1.0`, `release-v2.5`)
- **Generated version**: `X.Y.Z` where Z is auto-incremented

### Auto-Increment Logic
The workflow automatically finds the next available patch version:

| Branch Name | Existing Versions | Generated Version |
|-------------|-------------------|-------------------|
| `release-v1.0` | None | `1.0.0` |
| `release-v1.0` | `1.0.0` exists | `1.0.1` |
| `release-v1.0` | `1.0.0`, `1.0.1` exist | `1.0.2` |
| `release-v2.1` | None | `2.1.0` |

### Examples
```bash
# Create release branch for version 1.0.x
git checkout -b release-v1.0
git push origin release-v1.0  # → Publishes 1.0.0 (or 1.0.1 if 1.0.0 exists)

# Create release branch for version 2.5.x
git checkout -b release-v2.5
git push origin release-v2.5  # → Publishes 2.5.0 (or next available patch)
```

### Fallback
If the branch name doesn't match the `release-vX.Y` pattern, the workflow falls back to the version in `common.props`.

## Published Packages

The following NuGet packages are automatically published:

### Framework Packages
- `BBT.Aether.Core` - Core functionalities and base classes
- `BBT.Aether.Domain` - Domain entities, interfaces, and business rules
- `BBT.Aether.Application` - Application logic and business workflows
- `BBT.Aether.Infrastructure` - Infrastructure concerns (data access, caching, etc.)
- `BBT.Aether.AspNetCore` - ASP.NET Core extensions and utilities
- `BBT.Aether.TestBase` - Base classes for testing
- `BBT.Aether.HttpClient` - HTTP client utilities

### Module Packages
- `BBT.Aether.Modules.Asgard` - Asgard module
- `BBT.Aether.Modules.Fraud` - Fraud detection module

## Package Features

All packages include:
- ✅ XML documentation files
- ✅ Symbol packages (.snupkg) for debugging
- ✅ Source Link integration for source debugging
- ✅ Multi-target framework support (net8.0, net9.0, netstandard2.0, netstandard2.1)
- ✅ Comprehensive metadata (authors, description, tags, etc.)

## Using Packages from GitHub Packages

### Prerequisites
To consume packages from GitHub Packages, you need:
1. **GitHub account** with access to this repository
2. **Personal Access Token (PAT)** with `packages:read` permission

### 1. Create Personal Access Token
1. Go to GitHub → Settings → Developer settings → Personal access tokens → Tokens (classic)
2. Click "Generate new token (classic)"
3. Set expiration and select scope: `read:packages`
4. Copy the generated token

### 2. Configure NuGet Source

**Option A: Global Configuration (Recommended)**
```bash
# Add GitHub Packages as a NuGet source
dotnet nuget add source --username YOUR_GITHUB_USERNAME --password YOUR_PAT --store-password-in-clear-text --name github "https://nuget.pkg.github.com/OWNER/index.json"
```

**Option B: Project-specific (nuget.config)**
Create a `nuget.config` file in your project root:
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="github" value="https://nuget.pkg.github.com/OWNER/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <github>
      <add key="Username" value="YOUR_GITHUB_USERNAME" />
      <add key="ClearTextPassword" value="YOUR_PAT" />
    </github>
  </packageSourceCredentials>
</configuration>
```

### 3. Install Packages
```bash
# Install specific package
dotnet add package BBT.Aether.Core --version 1.0.0

# Install from specific source
dotnet add package BBT.Aether.Core --source github

# Restore packages
dotnet restore
```

### 4. Environment Variables (CI/CD)
For automated builds, use environment variables:
```bash
export NUGET_AUTH_TOKEN=your_pat_here
dotnet restore
```

### 5. Docker Support
In Dockerfile:
```dockerfile
# Set build argument
ARG GITHUB_TOKEN

# Configure NuGet source
RUN dotnet nuget add source --username docker --password ${GITHUB_TOKEN} --store-password-in-clear-text --name github "https://nuget.pkg.github.com/OWNER/index.json"

# Restore packages
COPY *.csproj ./
RUN dotnet restore
```

## Troubleshooting

### Workflow Fails with "Unauthorized"
- Verify workflow has `packages: write` permission
- Check if GitHub Packages is enabled in repository settings
- Ensure `GITHUB_TOKEN` has the necessary permissions (automatically provided)

### Package Already Exists Error
- The workflow uses `--skip-duplicate` flag to avoid conflicts
- GitHub Packages doesn't allow overwriting existing versions
- The auto-increment logic should prevent this, but if it occurs, check the version calculation

### Cannot Install Packages
- Verify Personal Access Token has `packages:read` permission
- Check if PAT is correctly configured in NuGet source
- Ensure you have access to the repository containing the packages
- Verify the package source URL is correct

### Build Failures
- Check that all dependencies are properly restored
- Verify project references are correct
- Review test failures in the workflow logs

### Missing Packages
- Ensure all project paths in the workflow match your repository structure
- Verify project files exist and are valid
- Check if any projects are excluded from packaging

## Branch Protection

Consider setting up branch protection rules for `release-v*` branches:
1. Go to Settings → Branches
2. Add rule for pattern `release-v*`
3. Enable "Require status checks to pass before merging"
4. Select the "Build and Publish NuGet Packages" check

## Local Testing

To test package creation locally:

```bash
# Build solutions
dotnet restore framework/BBT.Aether.sln
dotnet build framework/BBT.Aether.sln --configuration Release

# Create packages
dotnet pack framework/src/BBT.Aether.Core/BBT.Aether.Core.csproj --configuration Release --output ./test-packages

# Test package (dry run)
dotnet nuget push ./test-packages/*.nupkg --source https://api.nuget.org/v3/index.json --api-key YOUR_API_KEY --skip-duplicate --dry-run
```

## Support

If you encounter issues with the automated publishing:
1. Check the workflow logs in the Actions tab
2. Verify all secrets are properly configured
3. Ensure branch naming follows the `release-v*` pattern
4. Review the troubleshooting section above

For framework-specific issues, please refer to the main documentation or create an issue in the repository.
