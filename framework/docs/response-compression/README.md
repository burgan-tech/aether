# Response Compression

## Overview

Aether provides built-in HTTP response compression support using Gzip and Brotli algorithms. It reduces bandwidth usage and improves application performance with minimal configuration.

## Key Features

- **Multiple Algorithms** - Gzip and Brotli support
- **Configurable MIME Types** - Compress specific content types
- **HTTPS Support** - Optional compression for HTTPS
- **Exclusion Rules** - Exclude specific MIME types
- **Configuration-Driven** - Enable/disable via configuration

## Configuration

### appsettings.json

```json
{
  "ResponseCompression": {
    "Enable": true,
    "EnableForHttps": true,
    "Providers": ["brotli", "gzip"],
    "MimeTypes": [
      "application/json",
      "application/xml",
      "text/plain",
      "text/csv"
    ],
    "ExcludedMimeTypes": [
      "image/jpeg",
      "image/png",
      "video/mp4"
    ]
  }
}
```

### Service Registration

```csharp
// Automatically configured when using AddAetherAspNetCore
services.AddAetherAspNetCore();
```

### Middleware Registration

```csharp
var app = builder.Build();

// Add response compression middleware
app.UseAppResponseCompression();

// Must be before other middleware that generates responses
app.UseRouting();
app.MapControllers();

app.Run();
```

## Usage Examples

### Basic Setup

```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure services
builder.Services.AddAetherAspNetCore();

var app = builder.Build();

// Add compression middleware
app.UseAppResponseCompression();

app.MapGet("/api/products", async (IProductService service) =>
{
    var products = await service.GetAllAsync();
    return Results.Ok(products); // Response will be compressed
});

app.Run();
```

### Compression Providers

**Brotli (Recommended)**
- Better compression ratio
- Smaller response sizes
- Supported by modern browsers
- Preferred by browsers

**Gzip**
- Good compression ratio
- Wider browser support
- Faster compression/decompression
- Fallback for older browsers

### Provider Priority

Browsers specify preferred encoding in `Accept-Encoding` header:

```
Accept-Encoding: br, gzip, deflate
```

ASP.NET Core automatically selects the best available option:
1. Brotli (if supported and configured)
2. Gzip (if supported and configured)
3. No compression

## Default MIME Types

By default, ASP.NET Core compresses these types:
- text/plain
- text/css
- application/javascript
- text/html
- application/json
- application/xml
- text/xml

### Adding Custom MIME Types

```json
{
  "ResponseCompression": {
    "MimeTypes": [
      "application/vnd.api+json",
      "application/x-protobuf"
    ]
  }
}
```

### Excluding MIME Types

Already compressed formats should be excluded:

```json
{
  "ResponseCompression": {
    "ExcludedMimeTypes": [
      "image/jpeg",
      "image/png",
      "image/gif",
      "image/webp",
      "video/mp4",
      "video/webm",
      "audio/mpeg",
      "application/zip",
      "application/gzip"
    ]
  }
}
```

## Compression Levels

### Gzip Configuration

```csharp
services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Optimal; // Default in Aether
    // Other options: Fastest, SmallestSize
});
```

### Brotli Configuration

```csharp
services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Optimal; // Default in Aether
    // Other options: Fastest, SmallestSize
});
```

## Best Practices

### 1. Enable for HTTPS

```json
{
  "ResponseCompression": {
    "EnableForHttps": true
  }
}
```

Modern browsers handle compression over HTTPS securely.

### 2. Don't Compress Already Compressed Content

```csharp
// ❌ Bad: Compressing images/videos
{
  "MimeTypes": ["image/jpeg", "video/mp4"]
}

// ✅ Good: Exclude pre-compressed formats
{
  "ExcludedMimeTypes": ["image/jpeg", "video/mp4"]
}
```

### 3. Use Brotli When Possible

```json
{
  "Providers": ["brotli", "gzip"]
}
```

Brotli provides better compression but requires more CPU.

### 4. Place Middleware Early

```csharp
app.UseAppResponseCompression(); // First
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
```

## Performance Considerations

### CPU vs Bandwidth Trade-off

**Compression Level.Optimal:**
- Balanced CPU usage and compression ratio
- Recommended for most scenarios

**Compression Level.Fastest:**
- Lower CPU usage
- Larger response sizes
- Good for high-traffic scenarios

**CompressionLevel.SmallestSize:**
- Higher CPU usage
- Smallest response sizes
- Good for limited bandwidth

### When to Disable Compression

Consider disabling for:
- Very small responses (< 1 KB)
- Already compressed content
- Real-time/streaming data
- Low-latency requirements

## Compression Ratio Examples

### JSON Response (100 KB)

```
Original:     100 KB
Gzip:         ~20 KB (80% reduction)
Brotli:       ~15 KB (85% reduction)
```

### HTML Response (50 KB)

```
Original:     50 KB
Gzip:         ~10 KB (80% reduction)
Brotli:       ~8 KB (84% reduction)
```

## Conditional Compression

### Per-Action Compression

```csharp
[HttpGet("large-dataset")]
[ResponseCompression(Enabled = true)] // Force compression
public async Task<IActionResult> GetLargeDataset()
{
    var data = await _service.GetLargeDataAsync();
    return Ok(data);
}

[HttpGet("small-data")]
[ResponseCompression(Enabled = false)] // Disable compression
public IActionResult GetSmallData()
{
    return Ok(new { value = "small" });
}
```

## Monitoring

### Check Compression in Response

```http
HTTP/1.1 200 OK
Content-Type: application/json
Content-Encoding: br
Content-Length: 15234
Vary: Accept-Encoding
```

### Verify Compression Ratio

```csharp
// Original size in logs
_logger.LogInformation("Response size: {Size} bytes", response.Body.Length);

// Compare with Content-Length header (compressed size)
```

## Testing

### Testing Compression

```csharp
[Fact]
public async Task Api_ShouldCompressResponse()
{
    // Arrange
    var client = _factory.CreateClient();
    client.DefaultRequestHeaders.Add("Accept-Encoding", "br, gzip");
    
    // Act
    var response = await client.GetAsync("/api/products");
    
    // Assert
    Assert.True(response.Headers.Contains("Content-Encoding"));
    var encoding = response.Headers.GetValues("Content-Encoding").First();
    Assert.Contains(encoding, new[] { "br", "gzip" });
}
```

## Common Issues

### Issue: Responses not compressed

**Causes:**
1. Middleware not registered
2. Compression disabled in config
3. MIME type not included
4. Response too small (< 1 KB default)

**Solutions:**
```csharp
// 1. Register middleware
app.UseAppResponseCompression();

// 2. Check configuration
{
  "ResponseCompression": { "Enable": true }
}

// 3. Add MIME type
{
  "MimeTypes": ["application/json"]
}
```

### Issue: No compression over HTTPS

**Solution:**
```json
{
  "ResponseCompression": {
    "EnableForHttps": true
  }
}
```

## Related Features

- **[ASP.NET Core Integration](../README.md)** - Part of core ASP.NET setup

