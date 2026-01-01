using System.Windows.Forms;
using SshAgentProxy.Protocol;

namespace SshAgentProxy.UI;

public class KeySelectionDialog : Form
{
    private readonly ListBox _keyListBox;
    private readonly Button _okButton;
    private readonly Button _cancelButton;
    private readonly Label _timerLabel;
    private readonly System.Windows.Forms.Timer _countdownTimer;
    private int _remainingSeconds;
    private readonly List<SshIdentity> _keys;
    private readonly Dictionary<string, string> _keyToAgent;

    public List<SshIdentity>? SelectedKeys { get; private set; }

    public KeySelectionDialog(List<SshIdentity> keys, Dictionary<string, string> keyToAgent, int timeoutSeconds = 30)
    {
        _keys = keys;
        _keyToAgent = keyToAgent;
        _remainingSeconds = timeoutSeconds;

        Text = "SSH Key Selection";
        Width = 500;
        Height = 350;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;

        var label = new Label
        {
            Text = "Select SSH key(s) to use:",
            Location = new System.Drawing.Point(12, 12),
            AutoSize = true
        };
        Controls.Add(label);

        _keyListBox = new ListBox
        {
            Location = new System.Drawing.Point(12, 35),
            Width = 460,
            Height = 200,
            SelectionMode = SelectionMode.MultiExtended
        };

        foreach (var key in keys)
        {
            var agent = keyToAgent.TryGetValue(key.Fingerprint, out var a) ? a : "?";
            _keyListBox.Items.Add($"[{agent}] {key.Comment} ({key.Fingerprint})");
        }

        if (_keyListBox.Items.Count > 0)
            _keyListBox.SelectedIndex = 0;

        Controls.Add(_keyListBox);

        _timerLabel = new Label
        {
            Text = $"Auto-select in {_remainingSeconds}s",
            Location = new System.Drawing.Point(12, 245),
            AutoSize = true,
            ForeColor = System.Drawing.Color.Gray
        };
        Controls.Add(_timerLabel);

        _okButton = new Button
        {
            Text = "OK",
            Location = new System.Drawing.Point(316, 270),
            Width = 75,
            DialogResult = DialogResult.OK
        };
        _okButton.Click += OnOkClick;
        Controls.Add(_okButton);

        _cancelButton = new Button
        {
            Text = "Cancel",
            Location = new System.Drawing.Point(397, 270),
            Width = 75,
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(_cancelButton);

        AcceptButton = _okButton;
        CancelButton = _cancelButton;

        _countdownTimer = new System.Windows.Forms.Timer
        {
            Interval = 1000
        };
        _countdownTimer.Tick += OnTimerTick;
        _countdownTimer.Start();

        // Handle key events
        _keyListBox.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                OnOkClick(s, e);
                DialogResult = DialogResult.OK;
                Close();
            }
        };

        // Double-click to select
        _keyListBox.DoubleClick += (s, e) =>
        {
            OnOkClick(s, e);
            DialogResult = DialogResult.OK;
            Close();
        };
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _remainingSeconds--;
        _timerLabel.Text = $"Auto-select in {_remainingSeconds}s";

        if (_remainingSeconds <= 0)
        {
            _countdownTimer.Stop();
            OnOkClick(sender, e);
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private void OnOkClick(object? sender, EventArgs e)
    {
        _countdownTimer.Stop();
        SelectedKeys = new List<SshIdentity>();

        foreach (int index in _keyListBox.SelectedIndices)
        {
            SelectedKeys.Add(_keys[index]);
        }

        // If nothing selected, use first item
        if (SelectedKeys.Count == 0 && _keys.Count > 0)
        {
            SelectedKeys.Add(_keys[0]);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _countdownTimer.Stop();
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Show the dialog and return selected keys (thread-safe, works from non-UI thread)
    /// </summary>
    public static List<SshIdentity>? ShowDialog(
        List<SshIdentity> keys,
        Dictionary<string, string> keyToAgent,
        int timeoutSeconds = 30)
    {
        List<SshIdentity>? result = null;

        // Must run on STA thread for Windows Forms
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            using var dialog = new KeySelectionDialog(keys, keyToAgent, timeoutSeconds);
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                result = dialog.SelectedKeys;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return result;
    }
}
