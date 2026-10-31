using HashRelay.Cli;
using HashRelay.Client;

Terminal.Init();
var args_ = Args.Parse(args);
Terminal.Json = args_.Has("json");
Terminal.Quiet = args_.Has("quiet", "q");

if (args_.Has("version", "v") && args_.Positionals.Count == 0)
{
    Console.WriteLine($"hashrelay {Cli.Version}");
    return ExitCodes.Ok;
}

var command = args_.Positional(0)?.ToLowerInvariant();
var wantsHelp = args_.Has("help", "h") || command == "help";
if (command == "help") command = args_.Positional(1)?.ToLowerInvariant();
if (command is null || wantsHelp)
{
    if (command is not null && Help.Find(command) is { } doc) { Help.PrintCommand(doc); return ExitCodes.Ok; }
    Help.PrintOverview();
    return command is null && !wantsHelp ? ExitCodes.InvalidInput : ExitCodes.Ok;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    return await Cli.RunAsync(command, args_, cts.Token);
}
catch (OperationCanceledException)
{
    return ExitCodes.Ok;
}
catch (HashRelayApiException ex)
{
    return Cli.MapApiError(ex);
}
catch (HttpRequestException ex)
{
    return Terminal.Fail(ExitCodes.ServerUnavailable, $"Could not reach the HashRelay server: {ex.Message}", "Check `hashrelay status` and your network connection.");
}
catch (Exception ex)
{
    return Terminal.Fail(ExitCodes.Unexpected, $"Unexpected failure: {ex.Message}", ex.GetType().Name);
}
