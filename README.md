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
- Merged key listing from both agents
- Auto-configures `SSH_AUTH_SOCK` environment variable
- No manual intervention needed after initial setup
- Persists key-to-agent mappings for faster subsequent operations

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

To remove the proxy and restore default SSH agent behavior:

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
    "onePassword": {
      "name": "1Password",
      "processName": "1Password",
      "exePath": "C:\\Users\\...\\AppData\\Local\\1Password\\app\\8\\1Password.exe"
    },
    "bitwarden": {
      "name": "Bitwarden",
      "processName": "Bitwarden",
      "exePath": "C:\\Users\\...\\AppData\\Local\\Programs\\Bitwarden\\Bitwarden.exe"
    }
  },
  "keyMappings": [],
  "defaultAgent": "1Password"
}
```

### Key Mappings

You can pre-configure key-to-agent mappings:

```json
{
  "keyMappings": [
    { "fingerprint": "A1B2C3D4E5F6...", "agent": "1Password" },
    { "comment": "work@company.com", "agent": "Bitwarden" }
  ]
}
```

## How It Works

1. **Proxy Pipe**: Creates `\\.\pipe\ssh-agent-proxy` for SSH clients
2. **Backend Pipe**: Forwards requests to `\\.\pipe\openssh-ssh-agent` (owned by the active agent)
3. **Key Discovery**: On first identity request, scans both agents to build a complete key list
4. **Smart Routing**: When signing, checks which agent owns the key and switches if necessary
5. **Process Management**: Uses PowerShell CIM to terminate processes across sessions when switching agents
6. **Both Agents Running**: After signing, automatically starts the secondary agent so both are available

### Agent Switching Flow

```
SSH Client → Proxy → Check key ownership → Switch agent if needed → Backend Agent → Sign → Response
```

When switching agents:
1. Kill both agent processes (releases the pipe)
2. Start target agent (acquires the pipe)
3. Optionally start secondary agent (won't affect pipe ownership)

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

## License

MIT License - see [LICENSE](LICENSE) file for details.
