namespace BBT.Aether.Logging;

public interface IInitLoggerFactory
{
    IInitLogger<T> Create<T>();
}