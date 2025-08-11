using System;
using Microsoft.Extensions.Logging;
using BBT.Aether.ExceptionHandling;
using BBT.Aether.Logging;

namespace BBT.Aether;

public class BusinessException(
    string? code = null,
    string? message = null,
    string? details = null,
    Exception? innerException = null,
    LogLevel logLevel = LogLevel.Warning)
    : Exception(message, innerException),
        IBusinessException,
        IHasErrorCode,
        IHasErrorDetails,
        IHasLogLevel
{
    public string? Code { get; set; } = code;

    public string? Details { get; set; } = details;

    public LogLevel LogLevel { get; set; } = logLevel;

    public BusinessException WithData(string name, object value)
    {
        Data[name] = value;
        return this;
    }
}