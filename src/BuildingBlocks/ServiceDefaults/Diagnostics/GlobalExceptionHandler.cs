using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StatementDelivery.ServiceDefaults.Logging;

namespace StatementDelivery.ServiceDefaults.Diagnostics;

/// <summary>
/// Turns any unhandled exception into an RFC 9457 problem document.
/// </summary>
/// <remarks>
/// <para>
/// Every response carries a <c>traceId</c>. That is the entire point of the handler: a customer
/// or a support agent can quote one opaque string and an engineer can pull the exact trace, with
/// its logs and its database spans, out of the telemetry backend. Without it, "it failed at about
/// half past two" is the whole bug report.
/// </para>
/// <para>
/// Outside Development the response body says nothing about the exception. Exception messages
/// routinely contain connection strings, file paths, SQL fragments and parameter values, and this
/// platform is one where a parameter value can be a customer identifier or a download token. The
/// detail still reaches the logs, where access is controlled; it does not reach the caller.
/// </para>
/// </remarks>
public sealed partial class GlobalExceptionHandler : IExceptionHandler
{
    private const string GenericDetail =
        "An unexpected error occurred. Quote the traceId when reporting this.";

    private readonly IProblemDetailsService _problemDetails;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="GlobalExceptionHandler"/> class.</summary>
    /// <param name="problemDetails">The problem details service.</param>
    /// <param name="environment">The host environment.</param>
    /// <param name="logger">Logger.</param>
    public GlobalExceptionHandler(
        IProblemDetailsService problemDetails,
        IHostEnvironment environment,
        ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        // A cancelled request is the client hanging up, not a server fault. Writing a response to
        // a socket that is already gone just turns one non-event into a spurious 500 on a graph.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        string traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        LogUnhandledException(
            _logger,
            exception,
            SensitiveDataRedactor.RedactPathIdentifiers(httpContext.Request.Path.Value) ?? "/",
            traceId);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.6.1",
                Title = "An unexpected error occurred.",
                Status = StatusCodes.Status500InternalServerError,
                Detail = _environment.IsDevelopment() ? exception.Message : GenericDetail,
                Instance = SensitiveDataRedactor.RedactPathIdentifiers(httpContext.Request.Path.Value),
            },
        }).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Error,
        Message = "Unhandled exception serving {RequestPath}. traceId={TraceId}")]
    private static partial void LogUnhandledException(
        ILogger logger,
        Exception exception,
        string requestPath,
        string traceId);
}
