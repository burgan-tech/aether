using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Http;
using BBT.Aether.Validation;

namespace BBT.Aether.ExceptionHandling;

public class DefaultExceptionToErrorInfoConverter(IServiceProvider serviceProvider) : IExceptionToErrorInfoConverter
{
    protected IServiceProvider ServiceProvider { get; } = serviceProvider;

    public ServiceErrorInfo Convert(Exception exception, Action<AetherExceptionHandlingOptions>? options = null)
    {
        var exceptionHandlingOptions = CreateDefaultOptions();
        options?.Invoke(exceptionHandlingOptions);

        var errorInfo = CreateErrorInfoWithoutCode(exception, exceptionHandlingOptions);

        if (exception is IHasErrorCode hasErrorCodeException)
        {
            errorInfo.Code = hasErrorCodeException.Code;
        }

        return errorInfo;
    }

    protected virtual ServiceErrorInfo CreateErrorInfoWithoutCode(Exception exception, AetherExceptionHandlingOptions options)
    {
        if (options.SendExceptionsDetailsToClients)
        {
            return CreateDetailedErrorInfoFromException(exception, options.SendStackTraceToClients);
        }

        exception = TryToGetActualException(exception);

        if (exception is AetherDbConcurrencyException)
        {
            return new ServiceErrorInfo("The data you have submitted has already changed by another user/client. Please discard the changes you've done and try from the beginning.");
        }

        if (exception is EntityNotFoundException)
        {
            return CreateEntityNotFoundError((exception as EntityNotFoundException)!);
        }

        var errorInfo = new ServiceErrorInfo();

        if (exception is IUserFriendlyException)
        {
            errorInfo.Message = exception.Message;
            errorInfo.Details = (exception as IHasErrorDetails)?.Details;
        }

        if (exception is IHasValidationErrors)
        {
            if (errorInfo.Message.IsNullOrEmpty())
            {
                errorInfo.Message = "Your request is not valid!";
            }

            if (errorInfo.Details.IsNullOrEmpty())
            {
                errorInfo.Details = GetValidationErrorNarrative((exception as IHasValidationErrors)!);
            }

            errorInfo.ValidationErrors = GetValidationErrorInfos((exception as IHasValidationErrors)!);
        }

        if (errorInfo.Message.IsNullOrEmpty())
        {
            errorInfo.Message = "An internal error occurred during your request!";
        }
        else
        {
            errorInfo.Message = exception.Message;
        }

        errorInfo.Data = exception.Data;

        return errorInfo;
    }
    
    protected virtual ServiceErrorInfo CreateEntityNotFoundError(EntityNotFoundException exception)
    {
        if (exception.EntityType != null)
        {
            return new ServiceErrorInfo(
                string.Format(
                    "There is no entity {0} with id = {1}!",
                    exception.EntityType.Name,
                    exception.Id
                )
            );
        }

        return new ServiceErrorInfo(exception.Message);
    }

    protected virtual Exception TryToGetActualException(Exception exception)
    {
        if (exception is AggregateException aggException && aggException.InnerException != null)
        {
            if (aggException.InnerException is AetherValidationException ||
                aggException.InnerException is EntityNotFoundException ||
                aggException.InnerException is IBusinessException)
            {
                return aggException.InnerException;
            }
        }

        return exception;
    }

    protected virtual ServiceErrorInfo CreateDetailedErrorInfoFromException(Exception exception, bool sendStackTraceToClients)
    {
        var detailBuilder = new StringBuilder();

        AddExceptionToDetails(exception, detailBuilder, sendStackTraceToClients);

        var errorInfo = new ServiceErrorInfo(exception.Message, detailBuilder.ToString(), data: exception.Data);

        if (exception is AetherValidationException)
        {
            errorInfo.ValidationErrors = GetValidationErrorInfos((exception as AetherValidationException)!);
        }

        return errorInfo;
    }

    protected virtual void AddExceptionToDetails(Exception exception, StringBuilder detailBuilder, bool sendStackTraceToClients)
    {
        //Exception Message
        detailBuilder.AppendLine(exception.GetType().Name + ": " + exception.Message);

        //Additional info for UserFriendlyException
        if (exception is IUserFriendlyException &&
            exception is IHasErrorDetails)
        {
            var details = ((IHasErrorDetails)exception).Details;
            if (!details.IsNullOrEmpty())
            {
                detailBuilder.AppendLine(details);
            }
        }

        //Additional info for AetherValidationException
        if (exception is AetherValidationException validationException)
        {
            if (validationException.ValidationErrors.Count > 0)
            {
                detailBuilder.AppendLine(GetValidationErrorNarrative(validationException));
            }
        }

        //Exception StackTrace
        if (sendStackTraceToClients && !string.IsNullOrEmpty(exception.StackTrace))
        {
            detailBuilder.AppendLine("STACK TRACE: " + exception.StackTrace);
        }

        //Inner exception
        if (exception.InnerException != null)
        {
            AddExceptionToDetails(exception.InnerException, detailBuilder, sendStackTraceToClients);
        }

        //Inner exceptions for AggregateException
        if (exception is AggregateException aggException)
        {
            if (aggException.InnerExceptions.IsNullOrEmpty())
            {
                return;
            }

            foreach (var innerException in aggException.InnerExceptions)
            {
                AddExceptionToDetails(innerException, detailBuilder, sendStackTraceToClients);
            }
        }
    }

    protected virtual ServiceValidationErrorInfo[] GetValidationErrorInfos(IHasValidationErrors validationException)
    {
        var validationErrorInfos = new List<ServiceValidationErrorInfo>();

        foreach (var validationResult in validationException.ValidationErrors)
        {
            var validationError = new ServiceValidationErrorInfo(validationResult.ErrorMessage!);

            if (validationResult.MemberNames != null && validationResult.MemberNames.Any())
            {
                validationError.Members = validationResult.MemberNames.Select(m => m.ToCamelCase()).ToArray();
            }

            validationErrorInfos.Add(validationError);
        }

        return validationErrorInfos.ToArray();
    }

    protected virtual string GetValidationErrorNarrative(IHasValidationErrors validationException)
    {
        var detailBuilder = new StringBuilder();
        detailBuilder.AppendLine("The following errors were detected during validation.");

        foreach (var validationResult in validationException.ValidationErrors)
        {
            detailBuilder.AppendFormat(" - {0}", validationResult.ErrorMessage);
            detailBuilder.AppendLine();
        }

        return detailBuilder.ToString();
    }

    protected virtual AetherExceptionHandlingOptions CreateDefaultOptions()
    {
        return new AetherExceptionHandlingOptions
        {
            SendExceptionsDetailsToClients = false,
            SendStackTraceToClients = false
        };
    }
}
