using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;

namespace SshAgentProxy.UI;

/// <summary>
/// System tray icon with context menu.
/// Runs on a dedicated STA thread with proper Windows Forms message loop.
/// </summary>
public class TrayIcon : IDisposable
{
    private readonly Thread _uiThread;
    private readonly ManualResetEventSlim _ready = new(false);
    private TrayApplicationContext? _context;
    private bool _disposed;

    // Win32 API for console window control
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    public event Func<string, CancellationToken, Task>? OnSwitchAgent;
    public event Func<CancellationToken, Task>? OnRescanKeys;
    public event Action? OnExit;

    public TrayIcon(string version)
    {
        _uiThread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Create context AFTER WinForms is initialized but inside Application.Run
            _context = new TrayApplicationContext(version, this, _ready);
            Application.Run(_context);
        });
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.IsBackground = true; // Background thread - main thread controls lifetime
        _uiThread.Start();

        // Don't wait - let it initialize in parallel
        // _ready.Wait();
    }

    internal void InvokeSwitchAgent(string agent)
    {
        Task.Run(async () =>
        {
            if (OnSwitchAgent != null)
                await OnSwitchAgent(agent, CancellationToken.None);
        });
    }

    internal void InvokeRescanKeys()
    {
        Task.Run(async () =>
        {
            if (OnRescanKeys != null)
                await OnRescanKeys(CancellationToken.None);
        });
    }

    internal void InvokeExit()
    {
        OnExit?.Invoke();
    }

    public void ToggleConsole()
    {
        // Console window APIs are thread-safe, call directly
        var consoleWindow = GetConsoleWindow();
        if (consoleWindow == IntPtr.Zero) return;

        _context?.ToggleConsoleState(consoleWindow);
    }

    public void HideConsole()
    {
        var consoleWindow = GetConsoleWindow();
        if (consoleWindow == IntPtr.Zero) return;

        _context?.HideConsoleWindow(consoleWindow);
    }

    public void ShowConsole()
    {
        var consoleWindow = GetConsoleWindow();
        if (consoleWindow == IntPtr.Zero) return;

        _context?.ShowConsoleWindow(consoleWindow);
    }

    public void UpdateHostMappingsMenu(IReadOnlyList<HostKeyMapping> mappings)
    {
        _context?.UpdateHostMappingsMenu(mappings);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _context?.Exit();
        _uiThread.Join(2000); // Wait up to 2 seconds for UI thread to exit
        _ready.Dispose();
    }

    /// <summary>
    /// ApplicationContext that owns the tray icon
    /// </summary>
    private sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _contextMenu;
        private readonly ToolStripMenuItem _showHideItem;
        private readonly ToolStripMenuItem _hostMappingsItem;
        private readonly TrayIcon _owner;
        private bool _consoleVisible = true;

        public TrayApplicationContext(string version, TrayIcon owner, ManualResetEventSlim ready)
        {
            _owner = owner;

            _contextMenu = new ContextMenuStrip();

            // Show/Hide Console
            _showHideItem = new ToolStripMenuItem("Hide Console");
            _showHideItem.Click += (s, e) => ToggleConsole();
            _contextMenu.Items.Add(_showHideItem);

            _contextMenu.Items.Add(new ToolStripSeparator());

            // Switch agents
            var switch1Password = new ToolStripMenuItem("Switch to 1Password");
            switch1Password.Click += (s, e) => _owner.InvokeSwitchAgent("1Password");
            _contextMenu.Items.Add(switch1Password);

            var switchBitwarden = new ToolStripMenuItem("Switch to Bitwarden");
            switchBitwarden.Click += (s, e) => _owner.InvokeSwitchAgent("Bitwarden");
            _contextMenu.Items.Add(switchBitwarden);

            _contextMenu.Items.Add(new ToolStripSeparator());

            // Rescan
            var rescan = new ToolStripMenuItem("Rescan Keys");
            rescan.Click += (s, e) => _owner.InvokeRescanKeys();
            _contextMenu.Items.Add(rescan);

            // Host Mappings
            _hostMappingsItem = new ToolStripMenuItem("Host Mappings");
            _contextMenu.Items.Add(_hostMappingsItem);

            _contextMenu.Items.Add(new ToolStripSeparator());

            // Exit
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (s, e) => _owner.InvokeExit();
            _contextMenu.Items.Add(exit);

            // Create NotifyIcon
            _notifyIcon = new NotifyIcon
            {
                Text = $"SSH Agent Proxy {version}",
                ContextMenuStrip = _contextMenu,
                Visible = true,
                Icon = GenerateKeyIcon()
            };

            _notifyIcon.DoubleClick += (s, e) => ToggleConsole();

            // Signal that we're ready
            ready.Set();
        }

        private static Icon GenerateKeyIcon()
        {
            const int size = 32;
            using var bitmap = new Bitmap(size, size);
            using var g = Graphics.FromImage(bitmap);

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Colors - Gold key
            var keyColor = Color.FromArgb(255, 200, 160, 60);
            var outlineColor = Color.FromArgb(255, 140, 100, 40);
            using var keyBrush = new SolidBrush(keyColor);
            using var outlinePen = new Pen(outlineColor, 1.5f);

            // Key head (circle)
            g.FillEllipse(keyBrush, 4, 4, 14, 14);
            g.DrawEllipse(outlinePen, 4, 4, 14, 14);

            // Key hole
            g.FillEllipse(Brushes.White, 8, 8, 6, 6);

            // Key shaft
            var shaftPoints = new PointF[]
            {
                new(16, 11), new(27, 22), new(27, 26), new(24, 26),
                new(24, 24), new(21, 24), new(21, 26), new(18, 26),
                new(18, 24), new(16, 24), new(16, 14),
            };
            g.FillPolygon(keyBrush, shaftPoints);
            g.DrawPolygon(outlinePen, shaftPoints);

            IntPtr hIcon = bitmap.GetHicon();
            return Icon.FromHandle(hIcon);
        }

        public void ToggleConsoleState(IntPtr consoleWindow)
        {
            if (_consoleVisible)
            {
                HideConsoleWindow(consoleWindow);
            }
            else
            {
                ShowConsoleWindow(consoleWindow);
            }
        }

        public void HideConsoleWindow(IntPtr consoleWindow)
        {
            if (!_consoleVisible) return;

            ShowWindow(consoleWindow, SW_HIDE);
            _consoleVisible = false;

            if (_contextMenu.InvokeRequired)
                _contextMenu.BeginInvoke(() => _showHideItem.Text = "Show Console");
            else
                _showHideItem.Text = "Show Console";
        }

        public void ShowConsoleWindow(IntPtr consoleWindow)
        {
            if (_consoleVisible) return;

            ShowWindow(consoleWindow, SW_SHOW);
            ShowWindow(consoleWindow, SW_RESTORE);
            SetForegroundWindow(consoleWindow);
            _consoleVisible = true;

            if (_contextMenu.InvokeRequired)
                _contextMenu.BeginInvoke(() => _showHideItem.Text = "Hide Console");
            else
                _showHideItem.Text = "Hide Console";
        }

        private void ToggleConsole()
        {
            var consoleWindow = GetConsoleWindow();
            if (consoleWindow == IntPtr.Zero) return;
            ToggleConsoleState(consoleWindow);
        }

        public void UpdateHostMappingsMenu(IReadOnlyList<HostKeyMapping> mappings)
        {
            if (_contextMenu.InvokeRequired)
            {
                _contextMenu.Invoke(() => UpdateHostMappingsMenu(mappings));
                return;
            }

            _hostMappingsItem.DropDownItems.Clear();

            if (mappings.Count == 0)
            {
                _hostMappingsItem.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
            }
            else
            {
                foreach (var mapping in mappings)
                {
                    var item = new ToolStripMenuItem(mapping.Pattern)
                    {
                        ToolTipText = mapping.Description ?? mapping.Fingerprint
                    };
                    _hostMappingsItem.DropDownItems.Add(item);
                }
            }
        }

        public void Exit()
        {
            if (_contextMenu.InvokeRequired)
            {
                _contextMenu.Invoke(Exit);
                return;
            }

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _contextMenu.Dispose();
            Application.ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _contextMenu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
