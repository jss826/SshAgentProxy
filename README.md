# SSH Agent Proxy

[![CI](https://github.com/jss826/SshAgentProxy/actions/workflows/ci.yml/badge.svg)](https://github.com/jss826/SshAgentProxy/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Release](https://img.shields.io/github/v/release/jss826/SshAgentProxy)](https://github.com/jss826/SshAgentProxy/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

[日本語版 README](README.ja.md)

A Windows SSH agent proxy that automatically switches between **1Password** and **Bitwarden** SSH agents based on which key is requested.

## Problem

On Windows, only one application can own the `\\.\pipe\openssh-ssh-agent` named pipe at a time. If you use SSH keys stored in both 1Password and Bitwarden, you need to manually switch between them - closing one application and opening the other.

## Solution

SSH Agent Proxy creates its own named pipe (`\\.\pipe\ssh-agent-proxy`) and acts as a proxy. When an SSH operation is requested:

1. **Key Listing**: Returns a merged list of keys from both 1Password and Bitwarden
2. **Signing**: Automatically switches to the correct agent that owns the requested key

## Features

- Automatic agent switching based on key fingerprint
- Merged key listing from all configured agents
- Auto-configures `SSH_AUTH_SOCK` environment variable (restored on exit)
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

1. Clone the repository:
   ```
   git clone https://github.com/jss826/SshAgentProxy.git
   ```

2. Build the project:
   ```
   dotnet build
   ```

3. Run the proxy:
   ```
   dotnet run
   ```

The proxy will automatically set `SSH_AUTH_SOCK` in your user environment variables. New terminal windows will use the proxy automatically.

## Usage

### Starting the Proxy

Simply run the application:

```
SshAgentProxy.exe
```

On first run, it will:
- Set `SSH_AUTH_SOCK=\\.\pipe\ssh-agent-proxy` in user environment variables
- Create a config file at `%APPDATA%\SshAgentProxy\config.json`
- Start listening for SSH agent requests

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

The proxy automatically restores `SSH_AUTH_SOCK` to its original value when it exits normally. For permanent removal:

```
SshAgentProxy.exe --uninstall
```

This removes `SSH_AUTH_SOCK` from user environment variables. New terminals will use the default Windows OpenSSH agent.

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
3. Wait for user authentication if needed (up to ~15 seconds)

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
