using System.Net.Sockets;
using System.Runtime.InteropServices;
using STlauncher.Relay;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(RelayOptions.Usage());
    return 0;
}

if (!RelayOptions.TryParse(args, Environment.GetEnvironmentVariable, out var options, out var error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(RelayOptions.Usage());
    return 2;
}

// Everything goes to standard output and nowhere else: under systemd that is the
// journal, and the lines carry counters only, never an address or a key.
static void Log(string message) => Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {message}");

await using var server = new RelayServer(options, Log);

try
{
    server.Start();
}
catch (SocketException ex)
{
    Console.Error.WriteLine($"Cannot listen on port {options.Port}: {ex.Message}");
    return 1;
}

var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

// Ctrl+C in a terminal, SIGTERM from systemd: both end the same way, with the listener
// closed and every tunnel dropped before the process exits.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};

using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stop.TrySetResult();
});

using var statsStop = new CancellationTokenSource();
var stats = PrintStatsAsync(server, options.StatsInterval, statsStop.Token);

await stop.Task;

statsStop.Cancel();
await stats;
await server.StopAsync();
return 0;

static async Task PrintStatsAsync(RelayServer server, TimeSpan interval, CancellationToken token)
{
    if (interval <= TimeSpan.Zero)
    {
        return;
    }

    try
    {
        while (true)
        {
            await Task.Delay(interval, token);

            var now = server.Stats;
            Log($"rooms={now.Rooms} connections={now.Connections} tunnels={now.Tunnels}");
        }
    }
    catch (OperationCanceledException)
    {
    }
}
