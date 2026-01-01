using SshAgentProxy;
using SshAgentProxy.UI;

// Command line arguments (processed before mutex check)
bool startMinimized = args.Contains("--minimized") || args.Contains("-m");

if (args.Length > 0)
{
    switch (args[0])
    {
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
            Console.WriteLine("  --minimized   Start minimized to system tray");
            Console.WriteLine("  -m            Same as --minimized");
            Console.WriteLine("  --uninstall   Remove SSH_AUTH_SOCK from user environment");
            Console.WriteLine("  --reset       Same as --uninstall");
            Console.WriteLine("  --help, -h    Show this help");
            return;
    }
}

// Prevent multiple instances using a named mutex
const string mutexName = "Global\\SshAgentProxy_SingleInstance";
using var mutex = new Mutex(true, mutexName, out bool createdNew);

if (!createdNew)
{
    Console.WriteLine("Error: SshAgentProxy is already running.");
    Console.WriteLine("Only one instance is allowed.");
    Environment.Exit(1);
}

// Handle --test-switch after mutex check (it starts a proxy)
if (args.Length > 0 && args[0] == "--test-switch")
{
    var targetAgent = args.Length > 1 ? args[1] : "1Password";
    var force = args.Contains("--force");
    await TestSwitchAsync(targetAgent, force);
    return;
}

var version = typeof(SshAgentProxyService).Assembly.GetName().Version;
var versionStr = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "";

var expectedSock = @"\\.\pipe\ssh-agent-proxy";
var currentSock = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK", EnvironmentVariableTarget.User);

// Set SSH_AUTH_SOCK user environment variable if not configured
if (string.IsNullOrEmpty(currentSock) || currentSock != expectedSock)
{
    Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", expectedSock, EnvironmentVariableTarget.User);
}

var configPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "SshAgentProxy",
    "config.json");

Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

var config = Config.LoadOrCreate(configPath);

// Check if console is available
bool consoleAvailable = true;
ConsoleUI? ui = null;
try
{
    _ = Console.KeyAvailable;
    _ = Console.WindowHeight;
    ui = new ConsoleUI();
    ui.Initialize(versionStr, configPath, expectedSock);
}
catch
{
    consoleAvailable = false;
    // Fallback to simple console output
    Console.WriteLine($"SSH Agent Proxy {versionStr}");
    Console.WriteLine($"Config: {configPath}");
    Console.WriteLine("(Running without interactive console)");
}

await using var proxy = new SshAgentProxyService(config);

if (ui != null)
{
    proxy.OnLog += ui.Log;
}
else
{
    proxy.OnLog += Console.WriteLine;
}

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Create tray icon
using var trayIcon = new TrayIcon(versionStr);
trayIcon.OnSwitchAgent += proxy.SwitchToAsync;
trayIcon.OnRescanKeys += proxy.ScanKeysAsync;
trayIcon.OnExit += () => cts.Cancel();
trayIcon.UpdateHostMappingsMenu(config.HostKeyMappings);

await proxy.StartAsync(cts.Token);

// Start key input handler on a dedicated thread
Thread? keyInputThread = null;
if (consoleAvailable && ui != null)
{
    keyInputThread = new Thread(() =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                // Poll for key availability
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(50);
                    continue;
                }

                var key = Console.ReadKey(intercept: true);

                switch (key.KeyChar)
                {
                    case '1':
                        _ = proxy.SwitchToAsync("1Password", cts.Token);
                        break;
                    case '2':
                        _ = proxy.SwitchToAsync("Bitwarden", cts.Token);
                        break;
                    case 'r':
                    case 'R':
                        _ = proxy.ScanKeysAsync(cts.Token);
                        break;
                    case 'h':
                    case 'H':
                        ui.ShowHostMappings(config.HostKeyMappings);
                        trayIcon.UpdateHostMappingsMenu(config.HostKeyMappings);
                        break;
                    case 'd':
                    case 'D':
                        var indexToDelete = ui.PromptDeleteMapping(config.HostKeyMappings);
                        if (indexToDelete.HasValue)
                        {
                            var removed = config.HostKeyMappings[indexToDelete.Value];
                            config.HostKeyMappings.RemoveAt(indexToDelete.Value);
                            config.Save();
                            ui.Log($"Deleted: {removed.Pattern}");
                            trayIcon.UpdateHostMappingsMenu(config.HostKeyMappings);
                        }
                        break;
                    case 'c':
                    case 'C':
                        ui.Refresh();
                        break;
                    case 't':
                    case 'T':
                        trayIcon.ToggleConsole();
                        break;
                    case 'q':
                    case 'Q':
                        cts.Cancel();
                        break;
                }
            }
            catch
            {
                break;
            }
        }
    });
    keyInputThread.IsBackground = true;
    keyInputThread.Start();
}

try
{
    // Wait for cancellation
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    // Normal termination
}

ui?.Shutdown();
Console.WriteLine("Shutting down...");
Console.WriteLine("Note: SSH_AUTH_SOCK remains set. Run with --uninstall to remove it.");

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
