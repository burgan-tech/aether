namespace BBT.Aether.Http;

public class ServiceErrorResponse
{
    public ServiceErrorInfo Error { get; set; }

    public ServiceErrorResponse(ServiceErrorInfo error)
    {
        Error = error;
    }
}