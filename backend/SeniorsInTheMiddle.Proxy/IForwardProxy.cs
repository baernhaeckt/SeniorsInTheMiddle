internal interface IForwardProxy
{
    void Dispose();

    Task HandleAsync(HttpContext context);
}