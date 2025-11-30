namespace BBT.Aether.Results;

/// <summary>
/// Centralized error code constants for consistent error handling across the framework.
/// </summary>
public static class ErrorCodes
{
    /// <summary>
    /// Error prefixes for categorizing errors.
    /// </summary>
    public static class Prefixes
    {
        /// <summary>
        /// No error occurred (successful operation).
        /// </summary>
        public const string None = "none";
        
        /// <summary>
        /// Validation errors (input validation, business rules).
        /// Maps to HTTP 400 Bad Request.
        /// </summary>
        public const string Validation = "validation";

        /// <summary>
        /// Not supported error.
        /// Maps to HTTP 400 Bad Request.
        /// </summary>
        public const string NotSupported = "notsupported";
        
        /// <summary>
        /// Conflict errors (duplicate key, concurrent modification).
        /// Maps to HTTP 409 Conflict.
        /// </summary>
        public const string Conflict = "conflict";
        
        /// <summary>
        /// Not found errors (resource does not exist).
        /// Maps to HTTP 404 Not Found.
        /// </summary>
        public const string NotFound = "notfound";
        
        /// <summary>
        /// Unauthorized errors (authentication required).
        /// Maps to HTTP 401 Unauthorized.
        /// </summary>
        public const string Unauthorized = "unauthorized";
        
        /// <summary>
        /// Forbidden errors (insufficient permissions).
        /// Maps to HTTP 403 Forbidden.
        /// </summary>
        public const string Forbidden = "forbidden";
        
        /// <summary>
        /// Dependency errors (external service failures).
        /// Maps to HTTP 502 Bad Gateway.
        /// </summary>
        public const string Dependency = "dependency";
        
        /// <summary>
        /// Transient errors (temporary failures, retry recommended).
        /// Maps to HTTP 503 Service Unavailable.
        /// </summary>
        public const string Transient = "transient";
        
        /// <summary>
        /// General failure errors (unexpected errors).
        /// Maps to HTTP 500 Internal Server Error.
        /// </summary>
        public const string Failure = "failure";
    }
    
    /// <summary>
    /// Common validation error codes.
    /// </summary>
    public static class Validation
    {
        /// <summary>
        /// Model validation failed (from data annotations).
        /// </summary>
        public const string ModelValidationFailed = "ModelValidation:Failed";
        
        /// <summary>
        /// Required field is missing.
        /// </summary>
        public const string Required = "Required";
        
        /// <summary>
        /// Invalid format.
        /// </summary>
        public const string InvalidFormat = "InvalidFormat";
        
        /// <summary>
        /// Value out of range.
        /// </summary>
        public const string OutOfRange = "OutOfRange";
    }
    
    /// <summary>
    /// Common authentication/authorization error codes.
    /// </summary>
    public static class Auth
    {
        /// <summary>
        /// User is not authenticated.
        /// </summary>
        public const string Unauthenticated = "Unauthenticated";
        
        /// <summary>
        /// Invalid credentials.
        /// </summary>
        public const string InvalidCredentials = "InvalidCredentials";
        
        /// <summary>
        /// Token is expired.
        /// </summary>
        public const string TokenExpired = "TokenExpired";
        
        /// <summary>
        /// Insufficient permissions.
        /// </summary>
        public const string InsufficientPermissions = "InsufficientPermissions";
    }
    
    /// <summary>
    /// Common resource error codes.
    /// </summary>
    public static class Resource
    {
        /// <summary>
        /// Resource not found.
        /// </summary>
        public const string NotFound = "NotFound";
        
        /// <summary>
        /// Resource already exists.
        /// </summary>
        public const string AlreadyExists = "AlreadyExists";
        
        /// <summary>
        /// Resource is deleted.
        /// </summary>
        public const string Deleted = "Deleted";
    }
    
    /// <summary>
    /// Common general error codes.
    /// </summary>
    public static class General
    {
        /// <summary>
        /// Unexpected error occurred.
        /// </summary>
        public const string Unexpected = "Unexpected";
        
        /// <summary>
        /// Operation not implemented.
        /// </summary>
        public const string NotImplemented = "NotImplemented";
        
        /// <summary>
        /// Operation cancelled.
        /// </summary>
        public const string Cancelled = "Cancelled";
        
        /// <summary>
        /// Operation timed out.
        /// </summary>
        public const string Timeout = "Timeout";
        
        /// <summary>
        /// Concurrency error.
        /// </summary>
        public const string Concurrency = "Concurrency";
        
        /// <summary>
        /// User friendly error.
        /// </summary>
        public const string UserFriendly = "UserFriendly";
        
        /// <summary>
        /// Business rule error.
        /// </summary>
        public const string BusinessRule = "BusinessRule";
    }
}

