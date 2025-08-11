using System.Collections.Generic;

namespace BBT.Aether.AspNetCore.ResponseCompression;

public class ResponseCompressionOptions
{
    public bool Enable { get; set; }
    public List<string> Providers { get; set; } = ["gzip"];
    public bool EnableForHttps { get; set; }
    public List<string> MimeTypes { get; set; } = new();
    public List<string> ExcludedMimeTypes { get; set; } = new();
}