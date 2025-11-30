using System;
using Microsoft.Extensions.Logging;

namespace BBT.Aether;

public class ErrorException : BusinessException, IUserFriendlyException
{
    public ErrorException(
        string message,
        string? code = null,
        string? details = null,
        Exception? innerException = null,
        LogLevel logLevel = LogLevel.Warning)
        : base(
            code,
            message,
            details,
            innerException,
            logLevel)
    {
        Details = details;
    }
}