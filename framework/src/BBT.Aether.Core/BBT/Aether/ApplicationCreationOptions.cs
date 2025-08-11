using Microsoft.Extensions.Configuration;

namespace BBT.Aether;

public class ApplicationCreationOptions
{
    public AetherConfigurationBuilderOptions Configuration { get; } = new();

    public string? ApplicationName { get; set; }

    public string? Environment { get; set; }
}