namespace Aepw.Evidence.Worker.Models;

/// <summary>
/// Thrown for HTTP 5xx (transient) responses from the Backend API. Propagated out of the function
/// so Service Bus retries the message; after maxDeliveryCount it dead-letters.
/// </summary>
public sealed class TransientApiException : Exception
{
    public TransientApiException(string message) : base(message) { }
}

/// <summary>
/// Thrown for HTTP 4xx (permanent) responses. The Worker logs it and COMPLETES the message
/// (no retry) because re-sending the same bad input will never succeed.
/// </summary>
public sealed class PermanentApiException : Exception
{
    public PermanentApiException(string message) : base(message) { }
}
