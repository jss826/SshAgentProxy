namespace SshAgentProxy.UI;

/// <summary>
/// Console UI with fixed header and scrolling log area
/// </summary>
public class ConsoleUI
{
    private readonly object _lock = new();
    private readonly List<string> _logLines = new();
    private readonly int _headerHeight;
    private readonly int _maxLogLines;
    private string[] _headerLines = Array.Empty<string>();
    private bool _initialized = false;

    public ConsoleUI(int headerHeight = 12)
    {
        _headerHeight = headerHeight;
        _maxLogLines = 100; // Keep last 100 log lines in memory
    }

    public void Initialize(string version, string configPath, string sshAuthSock)
    {
        _headerLines = new[]
        {
            "+--------------------------------------+",
            $"|     SSH Agent Proxy {version,-17}|",
            "|     1Password / Bitwarden Switcher   |",
            "+--------------------------------------+",
            "",
            $"Config: {configPath}",
            $"SSH_AUTH_SOCK: {sshAuthSock}",
            "",
            "Commands: 1/2=switch  r=rescan  h=hosts  d=delete  t=tray  q=quit",
            new string('-', 60),
        };

        Console.Clear();
        Console.CursorVisible = false;
        DrawHeader();
        _initialized = true;
    }

    private void DrawHeader()
    {
        lock (_lock)
        {
            Console.SetCursorPosition(0, 0);
            foreach (var line in _headerLines)
            {
                // Pad to clear any previous content
                Console.WriteLine(line.PadRight(Console.WindowWidth - 1));
            }
        }
    }

    public void Log(string message)
    {
        if (!_initialized)
        {
            Console.WriteLine(message);
            return;
        }

        lock (_lock)
        {
            _logLines.Add(message);

            // Keep only last N lines
            while (_logLines.Count > _maxLogLines)
            {
                _logLines.RemoveAt(0);
            }

            RedrawLogArea();
        }
    }

    private void RedrawLogArea()
    {
        var logAreaStart = _headerLines.Length;
        var logAreaHeight = Console.WindowHeight - logAreaStart - 1;

        if (logAreaHeight <= 0) return;

        // Get the last N lines that fit in the log area
        var linesToShow = _logLines.TakeLast(logAreaHeight).ToList();

        for (int i = 0; i < logAreaHeight; i++)
        {
            Console.SetCursorPosition(0, logAreaStart + i);
            if (i < linesToShow.Count)
            {
                var line = linesToShow[i];
                // Truncate if too long
                if (line.Length > Console.WindowWidth - 1)
                    line = line[..(Console.WindowWidth - 4)] + "...";
                Console.Write(line.PadRight(Console.WindowWidth - 1));
            }
            else
            {
                Console.Write(new string(' ', Console.WindowWidth - 1));
            }
        }
    }

    public void Refresh()
    {
        if (!_initialized) return;

        lock (_lock)
        {
            Console.Clear();
            DrawHeader();
            RedrawLogArea();
        }
    }

    public void ShowHostMappings(IReadOnlyList<HostKeyMapping> mappings)
    {
        lock (_lock)
        {
            Log("");
            Log("=== Host Key Mappings ===");
            if (mappings.Count == 0)
            {
                Log("  (none)");
            }
            else
            {
                for (int i = 0; i < mappings.Count; i++)
                {
                    var m = mappings[i];
                    Log($"  [{i + 1}] {m.Pattern} -> {m.Fingerprint}");
                    if (!string.IsNullOrEmpty(m.Description))
                        Log($"      {m.Description}");
                }
            }
            Log("");
        }
    }

    public int? PromptDeleteMapping(IReadOnlyList<HostKeyMapping> mappings)
    {
        if (mappings.Count == 0)
        {
            Log("No host key mappings to delete.");
            return null;
        }

        lock (_lock)
        {
            Log("");
            Log("=== Delete Host Key Mapping ===");
            for (int i = 0; i < mappings.Count; i++)
            {
                var m = mappings[i];
                Log($"  [{i + 1}] {m.Pattern}");
            }
            Log("");
        }

        Console.CursorVisible = true;
        Console.Write("Enter number to delete (0=cancel): ");
        var input = Console.ReadLine();
        Console.CursorVisible = false;

        if (int.TryParse(input, out int index) && index > 0 && index <= mappings.Count)
        {
            return index - 1;
        }

        Refresh();
        return null;
    }

    public void Shutdown()
    {
        Console.CursorVisible = true;
        Console.Clear();
    }
}
