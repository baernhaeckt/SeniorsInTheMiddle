using SeniorsInTheMiddle.Proxy;
using SeniorsInTheMiddle.Proxy.Auth;
using SeniorsInTheMiddle.Proxy.Auth.Api;
using SeniorsInTheMiddle.Proxy.Forwarding;
using SeniorsInTheMiddle.Proxy.Services;
using SeniorsInTheMiddle.Proxy.Telemetry;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

// The proxy owns the listeners: plain HTTP for proxy clients, HTTPS for the dashboard.
builder.WebHost.ConfigureProxyKestrel(builder.Configuration);

// Register infrastructure services
builder.Services.AddSwaggerWithJwt();
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddAppCors(builder.Configuration);

// Register feature services
builder.Services.AddAuthServices();
builder.Services.AddForwardProxyServices();
builder.Services.AddTelemetryServices();
// The python services next to this process, one unix socket each (Services:*:SocketPath).
builder.Services.AddPythonServices(builder.Configuration);

WebApplication app = builder.Build();

// Proxy traffic is answered here and never reaches the dashboard pipeline.
app.UseForwardProxy();

// Register infrastructure middlewares
app.RegisterMiddlewares();

// A WebSocket handshake never reaches CORS, so the telemetry stream checks Origin itself.
app.UseTelemetryOriginGuard();

// Register application endpoints
app.RegisterAuthEndpoints();
app.RegisterProxyEndpoints();
app.MapTelemetryHub();
app.MapServiceHealth();

app.Run();
