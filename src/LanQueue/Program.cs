using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return await LanQueue.Cli.RunAsync(args, Console.Out, Console.Error, cts.Token);
}
catch (OperationCanceledException)
{
    return 130;
}
