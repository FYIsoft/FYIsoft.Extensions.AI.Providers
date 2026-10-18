using System.Net;

namespace Microsoft.Extensions.AI.Decision1;

/// <summary>
/// Base type for every failure the Decision1 client reports, except invalid arguments
/// (<see cref="ArgumentException"/>) and caller cancellation (<see cref="OperationCanceledException"/>).
/// </summary>
public class Decision1Exception : Exception
{
    /// <summary>Creates the exception.</summary>
    public Decision1Exception(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an inner exception.</summary>
    public Decision1Exception(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// The service returned a non-success HTTP status. Status-specific subclasses exist for 400, 401, 403,
/// 404, 422, 429 and 5xx; other statuses (for example 408 after retries) use this type.
/// </summary>
public class Decision1ApiException : Decision1Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="statusCode">The HTTP status.</param>
    /// <param name="errorCode">The error code from the body (<c>error.code</c>) or the <c>x-ms-error-code</c> header.</param>
    /// <param name="errorMessage">The service's message (<c>error.message</c>), or the HTTP reason when the body has none.</param>
    /// <param name="details">The service's details (<c>error.details</c>): its text, or raw JSON when it is not a string.</param>
    /// <param name="requestId">The request id header.</param>
    /// <param name="metadata">The response metadata.</param>
    public Decision1ApiException(HttpStatusCode statusCode, string? errorCode, string errorMessage, string? details, string? requestId, Decision1ResponseMetadata? metadata = null)
        : base(FormatMessage(statusCode, errorCode, errorMessage, requestId))
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        Details = details;
        RequestId = requestId;
        Metadata = metadata;
    }

    /// <summary>The HTTP status.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>The service error code, for example <c>unsupported_request_argument</c> or <c>DeploymentNotFound</c>.</summary>
    public string? ErrorCode { get; }

    /// <summary>The service's error message without SDK formatting.</summary>
    public string ErrorMessage { get; }

    /// <summary>The service's error details.</summary>
    public string? Details { get; }

    /// <summary>The request id to quote when contacting support.</summary>
    public string? RequestId { get; }

    /// <summary>Response metadata (region, rate limits, attempts), when available.</summary>
    public Decision1ResponseMetadata? Metadata { get; }

    private static string FormatMessage(HttpStatusCode status, string? code, string message, string? requestId) =>
        $"Decision1 request failed with HTTP {(int)status}{(code is null ? "" : $" ({code})")}: {message}" +
        (requestId is null ? "" : $" Request id: {requestId}.");
}

/// <summary>HTTP 400: the request has an invalid question type or shape.</summary>
public class Decision1BadRequestException(string? errorCode, string errorMessage, string? details, string? requestId, Decision1ResponseMetadata? metadata = null)
    : Decision1ApiException(HttpStatusCode.BadRequest, errorCode, errorMessage, details, requestId, metadata);

/// <summary>HTTP 401: the credential is missing, invalid or expired.</summary>
public class Decision1AuthenticationException(string? errorCode, string errorMessage, string? details, string? requestId, Decision1ResponseMetadata? metadata = null)
    : Decision1ApiException(HttpStatusCode.Unauthorized, errorCode, errorMessage, details, requestId, metadata);

/// <summary>HTTP 403: the identity has no access to the deployment.</summary>
public class Decision1PermissionDeniedException(string? errorCode, string errorMessage, string? details, string? requestId, Decision1ResponseMetadata? metadata = null)
    : Decision1ApiException(HttpStatusCode.Forbidden, errorCode, errorMessage, details, requestId, metadata);

/// <summary>HTTP 404: the endpoint, path or deployment does not exist (error code <c>DeploymentNotFound</c> for an unknown deployment).</summary>
public class Decision1NotFoundException(string? errorCode, string errorMessage, string? details, string? requestId, Decision1ResponseMetadata? metadata = null)
    : Decision1ApiException(HttpStatusCode.NotFound, errorCode, errorMessage, details, requestId, metadata);

/// <summary>HTTP 422: the deployment or a question definition is not supported.</summary>
public class Decision1UnprocessableEntityException(string? errorCode, string errorMessage, string? details, string? requestId, Decision1ResponseMetadata? metadata = null)
    : Decision1ApiException(HttpStatusCode.UnprocessableEntity, errorCode, errorMessage, details, requestId, metadata);

/// <summary>HTTP 429: the deployment's rate limit was exceeded and retries were exhausted.</summary>
public class Decision1RateLimitException(string? errorCode, string errorMessage, string? details, string? requestId, TimeSpan? retryAfter, Decision1ResponseMetadata? metadata = null)
    : Decision1ApiException(HttpStatusCode.TooManyRequests, errorCode, errorMessage, details, requestId, metadata)
{
    /// <summary>
    /// How long the service asked the caller to wait (<c>retry-after-ms</c>, <c>Retry-After</c> or
    /// <c>x-ratelimit-reset-requests</c>), uncapped. Null when the response gave no hint.
    /// </summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>HTTP 5xx: the service failed and retries were exhausted.</summary>
public class Decision1ServerException(HttpStatusCode statusCode, string? errorCode, string errorMessage, string? details, string? requestId, Decision1ResponseMetadata? metadata = null)
    : Decision1ApiException(statusCode, errorCode, errorMessage, details, requestId, metadata);

/// <summary>The service could not be reached (DNS, TLS, connection reset) after all retries.</summary>
public class Decision1ConnectionException : Decision1Exception
{
    /// <summary>Creates the exception.</summary>
    public Decision1ConnectionException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>Every attempt exceeded the per-attempt timeout (<see cref="Decision1ClientOptions.Timeout"/>).</summary>
public class Decision1TimeoutException : Decision1ConnectionException
{
    /// <summary>Creates the exception.</summary>
    public Decision1TimeoutException(string message, TimeSpan timeout, Exception? innerException) : base(message, innerException) =>
        Timeout = timeout;

    /// <summary>The per-attempt timeout that elapsed.</summary>
    public TimeSpan Timeout { get; }
}
