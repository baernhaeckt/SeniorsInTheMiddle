using SeniorsInTheMiddle.Proxy;
using SeniorsInTheMiddle.Proxy.Auth;
using SeniorsInTheMiddle.Proxy.Auth.Api;
using SeniorsInTheMiddle.Proxy.Forwarding;

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

WebApplication app = builder.Build();

// Proxy traffic is answered here and never reaches the dashboard pipeline.
app.UseForwardProxy();

// Register infrastructure middlewares
app.RegisterMiddlewares();

// Register application endpoints
app.RegisterAuthEndpoints();
app.RegisterProxyEndpoints();

app.Run();
