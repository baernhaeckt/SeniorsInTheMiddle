using Backend.Web;
using Backend.Web.Auth;
using Backend.Web.Auth.Api;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

// Register infrastructure services
builder.Services.AddSwaggerWithJwt();
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddAppCors();

// Register feature services
builder.Services.AddAuthServices();

WebApplication app = builder.Build();

// Register infrastructure middlewares
app.RegisterMiddlewares();

// Register application endpoints
app.RegisterAuthEndpoints();

app.Run();
