using Microsoft.AspNetCore.SignalR;

namespace SeniorsInTheMiddle.Proxy.Telemetry;

/// <summary>
/// The telemetry stream the dashboard attaches to.
///
/// It is one-way: the hub declares no callable methods, and every frame is pushed as a
/// JSON string on "event". The string matters — the SignalR client would otherwise hand
/// the browser an already-deserialized object, and the protocol's polymorphic "type"
/// discriminator is only written when the payload is serialized through the base type.
///
/// A dashboard that connects late has missed whatever went past, so the only thing it
/// receives on connect is the hello frame. Traffic follows on its own.
/// </summary>
sealed class TelemetryHub : Hub
{
    private readonly TelemetryDescriptor descriptor;

    public TelemetryHub(TelemetryDescriptor descriptor)
    {
        this.descriptor = descriptor;
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("event", TelemetryJson.Serialize(descriptor.Hello));
        await base.OnConnectedAsync();
    }
}
