using System.Collections.Generic;
using System.Net;

namespace BBT.Aether.AspNetCore.ExceptionHandling;

public class AetherExceptionHttpStatusCodeOptions
{
    public IDictionary<string, HttpStatusCode> ErrorCodeToHttpStatusCodeMappings { get; } = new Dictionary<string, HttpStatusCode>();

    public void Map(string errorCode, HttpStatusCode httpStatusCode)
    {
        ErrorCodeToHttpStatusCodeMappings[errorCode] = httpStatusCode;
    }
}