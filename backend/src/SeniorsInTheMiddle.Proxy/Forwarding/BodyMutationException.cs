namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// A body mutation failed and the exchange was abandoned rather than forwarded.
///
/// Thrown only on the request leg. The mutation is what decides which parts of a body may leave,
/// so one that did not finish leaves no basis for sending anything: the exception travels up
/// through <see cref="ForwardProxyTransformer.TransformRequestAsync"/>, where YARP turns it into
/// <c>ForwarderError.RequestCreation</c> and a 502, and the destination is never contacted.
/// </summary>
public sealed class BodyMutationException(string message, Exception inner)
    : Exception(message, inner);
