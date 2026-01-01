# SSH Agent Proxy

[![CI](https://github.com/jss826/SshAgentProxy/actions/workflows/ci.yml/badge.svg)](https://github.com/jss826/SshAgentProxy/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Release](https://img.shields.io/github/v/release/jss826/SshAgentProxy)](https://github.com/jss826/SshAgentProxy/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

[日本語版 README](README.ja.md)

A Windows SSH agent proxy that automatically switches between **1Password** and **Bitwarden** SSH agents based on which key is requested.

## Problem

On Windows, both 1Password and Bitwarden use the same named pipe (`\\.\pipe\openssh-ssh-agent`) for their SSH agents. This pipe name is hardcoded and cannot be changed. Only one application can own the pipe at a time, and there is no API to determine which application currently owns it—the only way to find out is to query the pipe (scan or sign).

If you use SSH keys stored in both applications, you need to manually switch between them—closing one application and opening the other.

## Solution

SSH Agent Proxy creates its own named pipe (`\\.\pipe\ssh-agent-proxy`) and acts as a proxy. When an SSH operation is requested:

1. **Key Listing**: Returns a merged list of keys from both 1Password and Bitwarden
2. **Signing**: Automatically switches to the correct agent that owns the requested key

## Features

- Automatic agent switching based on key fingerprint
- Merged key listing from all configured agents
- Auto-configures `SSH_AUTH_SOCK` environment variable
- No manual intervention needed after initial setup
- Persists key-to-agent mappings for faster subsequent operations
- Minimizes agent restarts by detecting current agent from existing pipe
- Failure caching to avoid repeated failed attempts
- Supports any number of SSH agents (not limited to 1Password/Bitwarden)

## Requirements

- Windows 10/11
- .NET 10.0 Runtime
- 1Password with SSH Agent enabled
- Bitwarden with SSH Agent enabled (optional)

## Installation

### From Source

1. Clone the repository:
   ```
   git clone https://github.com/jss826/SshAgentProxy.git
   cd SshAgentProxy
   ```

2. Build and publish:
   ```
   dotnet publish -c Release -o ./publish
   ```

3. Run the proxy:
   ```
   ./publish/SshAgentProxy.exe
   ```

Optionally, add the `publish` folder to your PATH or copy the executable to a convenient location.

The proxy will automatically set `SSH_AUTH_SOCK` in your user environment variables. New terminal windows will use the proxy automatically.

## Usage

### Starting the Proxy

Simply run the application:

```
SshAgentProxy.exe
```

On startup, it will:
- Set `SSH_AUTH_SOCK=\\.\pipe\ssh-agent-proxy` in user environment variables
- Create a config file at `%APPDATA%\SshAgentProxy\config.json` (if not exists)
- Start listening for SSH agent requests

**Important**: The proxy modifies the `SSH_AUTH_SOCK` environment variable permanently. While the proxy is not running, SSH operations will fail because the configured pipe doesn't exist. To restore normal SSH agent behavior:
- Run `SshAgentProxy.exe --uninstall` to remove it permanently
- Or in PowerShell: `[Environment]::SetEnvironmentVariable("SSH_AUTH_SOCK", $null, "User")`
- For current session only: `Remove-Item Env:SSH_AUTH_SOCK`

### Interactive Commands

While running, you can use these keyboard shortcuts:
- `1` - Switch to 1Password
- `2` - Switch to Bitwarden
- `r` - Rescan keys from current agent
- `q` - Quit

### Command Line Options

```
SshAgentProxy.exe [options]

Options:
  (none)        Start the proxy server
  --uninstall   Remove SSH_AUTH_SOCK from user environment
  --reset       Same as --uninstall
  --help, -h    Show this help
```

### Uninstalling

The proxy does **not** restore `SSH_AUTH_SOCK` on exit. This is intentional—it allows you to restart the proxy without affecting existing terminals. To restore normal SSH agent behavior:

```
SshAgentProxy.exe --uninstall
```

This removes `SSH_AUTH_SOCK` from user environment variables. New terminals will use the default Windows OpenSSH agent (or whichever agent owns the `openssh-ssh-agent` pipe).

## Configuration

The config file is located at `%APPDATA%\SshAgentProxy\config.json`:

```json
{
  "proxyPipeName": "ssh-agent-proxy",
  "backendPipeName": "openssh-ssh-agent",
  "agents": {
    "1Password": {
      "processName": "1Password",
      "exePath": "C:\\Users\\...\\AppData\\Local\\1Password\\app\\8\\1Password.exe",
      "priority": 1
    },
    "Bitwarden": {
      "processName": "Bitwarden",
      "exePath": "C:\\Users\\...\\AppData\\Local\\Programs\\Bitwarden\\Bitwarden.exe",
      "priority": 2
    }
  },
  "keyMappings": [],
  "defaultAgent": "1Password",
  "failureCacheTtlSeconds": 60
}
```

### Adding More Agents

You can add any SSH agent that uses the Windows named pipe interface:

```json
{
  "agents": {
    "1Password": { "processName": "1Password", "exePath": "...", "priority": 1 },
    "Bitwarden": { "processName": "Bitwarden", "exePath": "...", "priority": 2 },
    "KeePassXC": { "processName": "KeePassXC", "exePath": "...", "priority": 3 }
  }
}
```

The `priority` field determines the order in which agents are tried when searching for a key (lower number = higher priority).

### Key Mappings

Key mappings are automatically saved when a successful signing operation occurs. You can also pre-configure mappings:

```json
{
  "keyMappings": [
    { "fingerprint": "A1B2C3D4E5F6...", "agent": "1Password" },
    { "comment": "work@company.com", "agent": "Bitwarden" }
  ]
}
```

The proxy uses these mappings to:
1. Detect which agent currently owns the backend pipe on startup
2. Route signing requests directly to the correct agent without trial-and-error

## How It Works

1. **Proxy Pipe**: Creates `\\.\pipe\ssh-agent-proxy` for SSH clients
2. **Backend Pipe**: Forwards requests to `\\.\pipe\openssh-ssh-agent` (owned by the active agent)
3. **Key Discovery**: On first identity request, scans agents to build a complete key list (cached for future use)
4. **Smart Routing**: When signing, checks which agent owns the key and switches if necessary
5. **Key Caching**: Stores key data in config for instant startup without rescanning

### Agent Switching Flow

```
SSH Client → Proxy → Check key mapping → Switch agent if needed → Wait for auth → Sign → Response
```

When switching agents:
1. Kill current agent process (releases the pipe)
2. Start target agent (acquires the pipe)
3. Trigger unlock prompt via key listing request (Bitwarden requires this)
4. Wait for user authentication if needed (up to ~30 seconds)

### Terminology

- **Scan (Key Listing)**: Requesting the list of available keys (`ssh-add -l`). The proxy uses this to discover keys and detect pipe ownership.
- **Sign**: Authenticating with a key during SSH connection (`ssh user@host`). The target agent must own the pipe and be unlocked.

### Agent Behavior Differences

The proxy's strategy is optimized based on observed differences between 1Password and Bitwarden:

| Behavior | 1Password | Bitwarden |
|----------|-----------|-----------|
| Key listing when locked | Returns keys | Requires unlock |
| Unlock prompt trigger | On sign request | On key listing only |
| Pipe acquisition on start | Takes if available | Steals even if taken |
| After other agent exits | Does not auto-acquire | Does not auto-acquire |

**Implications:**

- **Bitwarden unlock prompts**: Querying Bitwarden (even just listing keys) triggers an unlock prompt. The proxy minimizes Bitwarden interactions by using cached key mappings and process detection instead of pipe queries.
- **Agent switching requires key listing first**: Because Bitwarden only shows the unlock prompt on key listing (not on sign requests), the proxy must send a key listing request after switching to Bitwarden to trigger the unlock dialog before attempting to sign.
- **Pipe ownership detection**: Instead of querying the pipe (which would trigger Bitwarden unlock), the proxy infers ownership from process state:
  - Both running → Bitwarden owns the pipe (because it steals on start)
  - Only 1Password running → Check pipe with a lightweight scan (1Password responds without unlock); if no response, pipe may be orphaned
  - Only Bitwarden running → Bitwarden owns the pipe
  - Neither running → No one owns the pipe
- **Startup optimization**: If key mappings already reference 2+ different agents, the proxy skips the initial scan to avoid unnecessary Bitwarden unlock prompts. Cached data is used instead. If a signing request comes for an unknown key, the proxy will scan agents as needed. Press `r` to manually rescan.

## Troubleshooting

### SSH operations hang or fail

1. Check if the proxy is running
2. Verify `SSH_AUTH_SOCK` is set correctly: `echo $env:SSH_AUTH_SOCK`
3. Try restarting the proxy

### Keys not appearing

1. Make sure 1Password/Bitwarden SSH agent is enabled in their settings
2. Press `r` in the proxy to rescan keys
3. Check that the applications have SSH keys configured

### Permission denied

This usually means the key exists in a different agent. The proxy should switch automatically, but if it fails:
1. Check the proxy logs for errors
2. Manually switch using `1` or `2` keys
3. Verify the key exists in the target application

## Known Limitations

The following edge cases are not handled and may require manual intervention:

- **Stale pipe**: If an agent crashes, the named pipe may remain but become non-functional. Restart the proxy to resolve.
- **Rotated keys**: If you delete/recreate a key with the same fingerprint, stale mappings in `config.json` may cause routing issues. Manually edit the `keyMappings` array to remove outdated entries.
- **Multi-key operations**: If a single SSH operation requires keys from multiple agents (rare), only the first key's agent will be used.
- **Locked vaults**: If an agent's vault is locked, key listing may return empty. Unlock the vault and press `r` to rescan.
- **Initial scan with multiple agents**: During the first scan, if one agent already holds the pipe, keys from other agents may not be discovered until they are used for signing. The proxy learns key-to-agent mappings over time.

## License

MIT License - see [LICENSE](LICENSE) file for details.
