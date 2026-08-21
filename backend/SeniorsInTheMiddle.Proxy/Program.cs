WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(
        8080, 
        listen => listen.Use(
            next => connection => listen
            .ApplicationServices
            .GetRequiredService<ConnectProxyMiddleware>()
            .InvokeAsync(connection, next)));
});

builder.Services
    .AddHttpForwarder()
    .AddSingleton<IForwardProxy, ForwardProxy>()
    .AddSingleton<IStreamProxyFactory, StreamProxyFactory>()
    .AddSingleton<ConnectProxyMiddleware>();

WebApplication app = builder.Build();

app.MapMethods(
    "/{**path}", 
    ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE"],
    (IForwardProxy proxy, HttpContext context) => proxy.HandleAsync(context));

app.Run();
