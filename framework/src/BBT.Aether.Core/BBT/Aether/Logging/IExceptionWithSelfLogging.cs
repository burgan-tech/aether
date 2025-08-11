using Microsoft.Extensions.Logging;

namespace BBT.Aether.Logging;

public interface IExceptionWithSelfLogging
{
    void Log(ILogger logger);
}