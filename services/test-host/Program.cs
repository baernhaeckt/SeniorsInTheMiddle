using System.Diagnostics;
using System.Text.Json;
using ServiceRuntime.TestHost;

// Integration test host: talks to the python example_service over its unix
// socket and verifies the runtime contract end to end.

var socketPath = args.FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("SERVICE_SOCKET_PATH")
    ?? "/run/services/example-service.sock";

Console.WriteLine($"[test-host] connecting to {socketPath}");

await using var client = await ServiceSocketClient.ConnectAsync(socketPath, TimeSpan.FromSeconds(30));

Console.WriteLine("[test-host] connected");
Console.WriteLine();

var runner = new TestRunner();

runner.Add("builtin $ping answers", async () =>
{
    var result = await client.CallAsync("$ping");
    Assert.Equal(true, result.GetProperty("pong").GetBoolean());
    Assert.Equal("ExampleService", result.GetProperty("service").GetString());
});

runner.Add("builtin $info reports the protocol", async () =>
{
    var result = await client.CallAsync("$info");
    Assert.Equal("length-prefixed-json/1", result.GetProperty("protocol").GetString());
    Assert.Equal(socketPath, result.GetProperty("socket_path").GetString());
});

runner.Add("builtin $health is ok", async () =>
{
    var result = await client.CallAsync("$health");
    Assert.Equal("ok", result.GetProperty("status").GetString());
    Assert.True(result.GetProperty("uptime_seconds").GetDouble() >= 0, "uptime must be non-negative");
});

runner.Add("echo returns the payload unchanged", async () =>
{
    var result = await client.CallAsync("echo", new { message = "hoi", count = 3, nested = new { ok = true } });
    Assert.Equal("hoi", result.GetProperty("message").GetString());
    Assert.Equal(3, result.GetProperty("count").GetInt32());
    Assert.Equal(true, result.GetProperty("nested").GetProperty("ok").GetBoolean());
});

runner.Add("echo without a payload gets an empty object", async () =>
{
    var result = await client.CallAsync("echo");
    Assert.Equal(JsonValueKind.Object, result.ValueKind);
    Assert.Equal(0, result.EnumerateObject().Count());
});

runner.Add("greet uses the service state", async () =>
{
    // the umlaut stays escaped in the source and travels as utf-8 on the wire
    var result = await client.CallAsync("greet", new { name = "B\u00e4rn" });
    Assert.Equal("Hoi, B\u00e4rn!", result.GetProperty("message").GetString());
});

runner.Add("sum adds the numbers", async () =>
{
    var result = await client.CallAsync("sum", new { values = new[] { 1, 2, 3, 4 } });
    Assert.Equal(10, result.GetProperty("total").GetInt32());
});

runner.Add("unknown methods return method_not_found", async () =>
{
    var error = await Assert.ThrowsAsync(() => client.CallAsync("does-not-exist"));
    Assert.Equal("method_not_found", error.Code);
    Assert.Equal("does-not-exist", error.Details?.GetProperty("method").GetString());
});

runner.Add("bad payloads return invalid_request", async () =>
{
    var error = await Assert.ThrowsAsync(() => client.CallAsync("greet", new { name = 42 }));
    Assert.Equal("invalid_request", error.Code);
    Assert.Equal("name", error.Details?.GetProperty("field").GetString());
});

runner.Add("service errors keep their code and details", async () =>
{
    var error = await Assert.ThrowsAsync(() => client.CallAsync("fail", new { code = "teapot", message = "nope" }));
    Assert.Equal("teapot", error.Code);
    Assert.Equal("nope", error.ServiceMessage);
    Assert.Equal("fail", error.Details?.GetProperty("method").GetString());
});

runner.Add("a failed call does not break the connection", async () =>
{
    var result = await client.CallAsync("echo", new { still = "alive" });
    Assert.Equal("alive", result.GetProperty("still").GetString());
});

runner.Add("requests are answered out of order", async () =>
{
    var slow = client.CallAsync("slow", new { seconds = 0.75 });
    var fast = client.CallAsync("echo", new { fast = true });

    var first = await Task.WhenAny(slow, fast);
    Assert.True(ReferenceEquals(first, fast), "the fast call must finish while the slow one is still running");

    var slept = (await slow).GetProperty("slept_seconds").GetDouble();
    Assert.Equal(0.75, slept);
});

runner.Add("many parallel calls on one connection", async () =>
{
    var calls = Enumerable.Range(0, 50)
        .Select(i => client.CallAsync("sum", new { values = new[] { i, i } }))
        .ToArray();

    var results = await Task.WhenAll(calls);
    for (var i = 0; i < results.Length; i++)
    {
        Assert.Equal(i * 2, results[i].GetProperty("total").GetInt32());
    }
});

runner.Add("a second connection is served too", async () =>
{
    await using var second = await ServiceSocketClient.ConnectAsync(socketPath, TimeSpan.FromSeconds(5));
    var result = await second.CallAsync("greet", new { name = "second connection" });
    Assert.Equal("Hoi, second connection!", result.GetProperty("message").GetString());
});

runner.Add("large payloads survive the framing", async () =>
{
    var blob = new string('x', 512 * 1024);
    var result = await client.CallAsync("echo", new { blob });
    Assert.Equal(blob.Length, result.GetProperty("blob").GetString()!.Length);
});

runner.Add("stats show the handled requests", async () =>
{
    var result = await client.CallAsync("stats");
    Assert.True(result.GetProperty("handled_requests").GetInt32() > 50, "expected the service to have handled the calls above");
});

return await runner.RunAsync();

/// <summary>
/// Runs the named checks in order, printing a line each and returning a process exit code.
/// Deliberately hand-rolled: this host is a single file that talks to a live socket, and a
/// test framework would bring more setup than the checks themselves.
/// </summary>
internal sealed class TestRunner
{
    private readonly List<(string Name, Func<Task> Body)> _tests = [];

    public void Add(string name, Func<Task> body) => _tests.Add((name, body));

    public async Task<int> RunAsync()
    {
        var failed = 0;

        foreach (var (name, body) in _tests)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await body();
                Console.WriteLine($"  PASS  {name} ({stopwatch.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name} ({stopwatch.ElapsedMilliseconds} ms)");
                Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"[test-host] {_tests.Count - failed}/{_tests.Count} passed");
        return failed == 0 ? 0 : 1;
    }
}

/// <summary>The handful of assertions <see cref="TestRunner"/> needs, each throwing on failure.</summary>
internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"expected <{expected}>, got <{actual}>");
        }
    }

    public static async Task<ServiceCallException> ThrowsAsync(Func<Task<JsonElement>> call)
    {
        try
        {
            var result = await call();
            throw new InvalidOperationException($"expected a ServiceCallException, got <{ServiceSocketClient.Describe(result)}>");
        }
        catch (ServiceCallException ex)
        {
            return ex;
        }
    }
}
