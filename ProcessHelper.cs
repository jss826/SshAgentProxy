using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

namespace SshAgentProxy;

public static class ProcessHelper
{
    /// <summary>
    /// Get command line arguments for a process by PID
    /// </summary>
    public static string? GetCommandLine(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            using var results = searcher.Get();

            foreach (var obj in results)
            {
                return obj["CommandLine"]?.ToString();
            }
        }
        catch
        {
            // WMI query failed
        }
        return null;
    }

    /// <summary>
    /// Get process name by PID
    /// </summary>
    public static string? GetProcessName(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parse SSH command line to extract connection info
    /// </summary>
    public static SshConnectionInfo? ParseSshCommandLine(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
            return null;

        // Pattern: ssh [options] user@host command 'repo'
        // Example: ssh git@github.com git-upload-pack 'user/repo.git'
        // Example: ssh -o Option=value git@github.com git-upload-pack 'user/repo.git'

        var info = new SshConnectionInfo();

        // Extract user@host pattern
        var userHostMatch = Regex.Match(commandLine, @"[\s\""]([^@\s]+)@([^\s\"":]+)");
        if (userHostMatch.Success)
        {
            info.User = userHostMatch.Groups[1].Value;
            info.Host = userHostMatch.Groups[2].Value;
        }
        else
        {
            // Fallback: try to extract host-only pattern (e.g., "ssh soon-nixos")
            // Check for -l option for user
            var lOptionMatch = Regex.Match(commandLine, @"-l\s+(\S+)");
            if (lOptionMatch.Success)
            {
                info.User = lOptionMatch.Groups[1].Value;
            }

            // Extract host: look for ssh command followed by a hostname
            // Match: ssh [options] hostname [command]
            // Options with values: -l, -o, -i, -p, -F, -J, -W, -b, -c, -D, -E, -e, -I, -L, -m, -O, -Q, -R, -S, -w
            var sshMatch = Regex.Match(commandLine, @"ssh(?:\.exe)?""?\s+(.+)", RegexOptions.IgnoreCase);
            if (sshMatch.Success)
            {
                var args = sshMatch.Groups[1].Value;
                // Skip options and their values to find the host
                var optionsWithValues = new HashSet<string> { "-l", "-o", "-i", "-p", "-F", "-J", "-W", "-b", "-c", "-D", "-E", "-e", "-I", "-L", "-m", "-O", "-Q", "-R", "-S", "-w" };
                var parts = Regex.Matches(args, @"(?:""[^""]*""|\S)+").Cast<Match>().Select(m => m.Value).ToList();

                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    if (part.StartsWith("-"))
                    {
                        // Check if this option takes a value
                        if (optionsWithValues.Contains(part) && i + 1 < parts.Count)
                        {
                            i++; // Skip the value
                        }
                        continue;
                    }
                    // First non-option argument is the host
                    info.Host = part.Trim('"');
                    break;
                }
            }
        }

        // Extract git command (git-upload-pack for fetch/clone, git-receive-pack for push)
        if (commandLine.Contains("git-upload-pack"))
            info.GitCommand = "git-upload-pack";
        else if (commandLine.Contains("git-receive-pack"))
            info.GitCommand = "git-receive-pack";

        // Extract repository path (usually in single quotes)
        var repoMatch = Regex.Match(commandLine, @"'([^']+\.git)'|'([^']+)'");
        if (repoMatch.Success)
        {
            info.Repository = repoMatch.Groups[1].Success
                ? repoMatch.Groups[1].Value
                : repoMatch.Groups[2].Value;
        }

        // Also try without quotes (some SSH versions)
        if (string.IsNullOrEmpty(info.Repository))
        {
            var repoMatch2 = Regex.Match(commandLine, @"git-(?:upload|receive)-pack\s+(\S+)");
            if (repoMatch2.Success)
            {
                info.Repository = repoMatch2.Groups[1].Value.Trim('\'', '"');
            }
        }

        return info.Host != null ? info : null;
    }

    /// <summary>
    /// Try to get SSH connection info from a connecting client process
    /// </summary>
    public static SshConnectionInfo? GetSshConnectionInfo(int clientPid)
    {
        // First try the client process itself
        var commandLine = GetCommandLine(clientPid);
        var info = ParseSshCommandLine(commandLine);
        if (info != null)
            return info;

        // If client is not SSH directly, check parent process
        // (git.exe spawns ssh.exe, but ssh.exe connects to the agent)
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {clientPid}");
            using var results = searcher.Get();

            foreach (var obj in results)
            {
                var parentPid = Convert.ToInt32(obj["ParentProcessId"]);
                var parentCommandLine = GetCommandLine(parentPid);

                // Check if parent is git and has useful info
                if (parentCommandLine != null && parentCommandLine.Contains("git"))
                {
                    // Parse git command line for remote URL
                    info = ParseGitCommandLine(parentCommandLine);
                    if (info != null)
                        return info;
                }
            }
        }
        catch
        {
            // Parent process lookup failed
        }

        return null;
    }

    /// <summary>
    /// Parse git command line to extract remote info
    /// </summary>
    private static SshConnectionInfo? ParseGitCommandLine(string commandLine)
    {
        // Pattern: git clone git@host:user/repo.git
        // Pattern: git clone ssh://git@host/user/repo.git
        var match = Regex.Match(commandLine, @"git@([^:]+):([^\s]+)");
        if (match.Success)
        {
            return new SshConnectionInfo
            {
                Host = match.Groups[1].Value,
                User = "git",
                Repository = match.Groups[2].Value.TrimEnd('\'', '"')
            };
        }

        // SSH URL format
        match = Regex.Match(commandLine, @"ssh://([^@]+)@([^/]+)/(.+)");
        if (match.Success)
        {
            return new SshConnectionInfo
            {
                User = match.Groups[1].Value,
                Host = match.Groups[2].Value,
                Repository = match.Groups[3].Value.TrimEnd('\'', '"')
            };
        }

        return null;
    }
}

public class SshConnectionInfo
{
    public string? Host { get; set; }
    public string? User { get; set; }
    public string? Repository { get; set; }
    public string? GitCommand { get; set; }

    /// <summary>
    /// Get the repository owner (first part of path)
    /// </summary>
    public string? GetOwner()
    {
        if (string.IsNullOrEmpty(Repository))
            return null;

        var parts = Repository.TrimStart('/').Split('/');
        return parts.Length > 0 ? parts[0] : null;
    }

    /// <summary>
    /// Check if this connection matches a pattern like "github.com:owner/*" or "github.com:*"
    /// </summary>
    public bool MatchesPattern(string pattern)
    {
        if (string.IsNullOrEmpty(Host))
            return false;

        // Pattern format: "host:owner/*" or "host:*"
        var parts = pattern.Split(':', 2);
        if (parts.Length == 0)
            return false;

        var patternHost = parts[0];
        var patternPath = parts.Length > 1 ? parts[1] : "*";

        // Check host
        if (!string.Equals(Host, patternHost, StringComparison.OrdinalIgnoreCase))
            return false;

        // Check path pattern
        if (patternPath == "*")
            return true;

        if (patternPath.EndsWith("/*"))
        {
            var patternOwner = patternPath[..^2];
            var actualOwner = GetOwner();
            return string.Equals(actualOwner, patternOwner, StringComparison.OrdinalIgnoreCase);
        }

        // Exact match (strip .git suffix properly)
        return string.Equals(StripGitSuffix(Repository),
            StripGitSuffix(patternPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? StripGitSuffix(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        return path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? path[..^4]
            : path;
    }

    public override string ToString()
    {
        return $"{User}@{Host}:{Repository} ({GitCommand})";
    }
}
