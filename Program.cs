using SshAgentProxy;

// Command line arguments
if (args.Length > 0)
{
    switch (args[0])
    {
        case "--test-switch":
            var targetAgent = args.Length > 1 ? args[1] : "1Password";
            var force = args.Contains("--force");
            await TestSwitchAsync(targetAgent, force);
            return;

        case "--uninstall":
        case "--reset":
            var current = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK", EnvironmentVariableTarget.User);
            if (!string.IsNullOrEmpty(current))
            {
                Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", null, EnvironmentVariableTarget.User);
                Console.WriteLine("SSH_AUTH_SOCK has been removed from user environment variables.");
                Console.WriteLine("New terminals will use the default SSH agent.");
            }
            else
            {
                Console.WriteLine("SSH_AUTH_SOCK was not set.");
            }
            return;

        case "--help":
        case "-h":
            Console.WriteLine("Usage: SshAgentProxy [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  (none)        Start the proxy server");
            Console.WriteLine("  --uninstall   Remove SSH_AUTH_SOCK from user environment");
            Console.WriteLine("  --reset       Same as --uninstall");
            Console.WriteLine("  --help, -h    Show this help");
            return;
    }
}

Console.WriteLine("╔══════════════════════════════════════╗");
Console.WriteLine("║     SSH Agent Proxy                  ║");
Console.WriteLine("║     1Password / Bitwarden Switcher   ║");
Console.WriteLine("╚══════════════════════════════════════╝");
Console.WriteLine();

// Save original SSH_AUTH_SOCK value for restoration on exit
var expectedSock = @"\\.\pipe\ssh-agent-proxy";
var originalSock = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK", EnvironmentVariableTarget.User);

// Set SSH_AUTH_SOCK user environment variable if not configured
if (string.IsNullOrEmpty(originalSock) || originalSock != expectedSock)
{
    Console.WriteLine($"Setting SSH_AUTH_SOCK environment variable...");
    Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", expectedSock, EnvironmentVariableTarget.User);
    Console.WriteLine($"  SSH_AUTH_SOCK = {expectedSock}");
    Console.WriteLine($"  (New terminals will use this automatically)");
    Console.WriteLine();
}
else
{
    Console.WriteLine($"SSH_AUTH_SOCK already configured: {originalSock}");
    Console.WriteLine();
}

var configPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "SshAgentProxy",
    "config.json");

Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
Console.WriteLine($"Config: {configPath}");
Console.WriteLine();

var config = Config.LoadOrCreate(configPath);

await using var proxy = new SshAgentProxyService(config);
proxy.OnLog += Console.WriteLine;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await proxy.StartAsync(cts.Token);

Console.WriteLine();
Console.WriteLine("Commands: '1' = switch to 1Password, '2' = switch to Bitwarden, 'r' = rescan keys, 'q' = quit");
Console.WriteLine("Press Ctrl+C to stop");
Console.WriteLine();

// Check if console is available
bool consoleAvailable = true;
try
{
    _ = Console.KeyAvailable;
}
catch
{
    consoleAvailable = false;
    Console.WriteLine("(Running without interactive console)");
}

try
{
    if (consoleAvailable)
    {
        while (!cts.Token.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                switch (key.KeyChar)
                {
                    case '1':
                        await proxy.SwitchToAsync("1Password", cts.Token);
                        break;
                    case '2':
                        await proxy.SwitchToAsync("Bitwarden", cts.Token);
                        break;
                    case 'r':
                    case 'R':
                        await proxy.ScanKeysAsync(cts.Token);
                        break;
                    case 'q':
                    case 'Q':
                        cts.Cancel();
                        break;
                }
            }
            await Task.Delay(100, cts.Token);
        }
    }
    else
    {
        // Wait indefinitely if no console
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
}
catch (OperationCanceledException)
{
    // Normal termination
}

Console.WriteLine();
Console.WriteLine("Shutting down...");

// Restore original SSH_AUTH_SOCK if we changed it
var currentSockOnExit = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK", EnvironmentVariableTarget.User);
if (currentSockOnExit == expectedSock)
{
    if (string.IsNullOrEmpty(originalSock) || originalSock == expectedSock)
    {
        // Original was empty or same as proxy - remove it
        Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", null, EnvironmentVariableTarget.User);
        Console.WriteLine("SSH_AUTH_SOCK removed (restored to default)");
    }
    else
    {
        // Restore the original value
        Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", originalSock, EnvironmentVariableTarget.User);
        Console.WriteLine($"SSH_AUTH_SOCK restored to: {originalSock}");
    }
}

// Test function
async Task TestSwitchAsync(string targetAgent, bool force)
{
    Console.WriteLine($"=== Testing switch to {targetAgent} (force={force}) ===");
    Console.WriteLine();

    var testConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SshAgentProxy",
        "config.json");
    var testConfig = Config.LoadOrCreate(testConfigPath);

    await using var testProxy = new SshAgentProxyService(testConfig);
    testProxy.OnLog += Console.WriteLine;

    Console.WriteLine("Attempting switch...");
    Console.WriteLine();

    if (force)
    {
        await testProxy.ForceSwitchToAsync(targetAgent, CancellationToken.None);
    }
    else
    {
        await testProxy.SwitchToAsync(targetAgent, CancellationToken.None);
    }

    Console.WriteLine();
    Console.WriteLine("=== Switch complete ===");
}
