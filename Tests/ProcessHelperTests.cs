namespace SshAgentProxy.Tests;

public class SshConnectionInfoTests
{
    #region GetOwner Tests

    [Fact]
    public void GetOwner_WithOwnerAndRepo_ReturnsOwner()
    {
        var info = new SshConnectionInfo { Repository = "owner/repo.git" };
        Assert.Equal("owner", info.GetOwner());
    }

    [Fact]
    public void GetOwner_WithLeadingSlash_ReturnsOwner()
    {
        var info = new SshConnectionInfo { Repository = "/owner/repo.git" };
        Assert.Equal("owner", info.GetOwner());
    }

    [Fact]
    public void GetOwner_WithOnlyRepo_ReturnsRepo()
    {
        var info = new SshConnectionInfo { Repository = "repo.git" };
        Assert.Equal("repo.git", info.GetOwner());
    }

    [Fact]
    public void GetOwner_WithNull_ReturnsNull()
    {
        var info = new SshConnectionInfo { Repository = null };
        Assert.Null(info.GetOwner());
    }

    [Fact]
    public void GetOwner_WithEmpty_ReturnsNull()
    {
        var info = new SshConnectionInfo { Repository = "" };
        Assert.Null(info.GetOwner());
    }

    #endregion

    #region MatchesPattern Tests - Host Matching

    [Fact]
    public void MatchesPattern_MatchingHost_ReturnsTrue()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = "owner/repo.git" };
        Assert.True(info.MatchesPattern("github.com:*"));
    }

    [Fact]
    public void MatchesPattern_DifferentHost_ReturnsFalse()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = "owner/repo.git" };
        Assert.False(info.MatchesPattern("gitlab.com:*"));
    }

    [Fact]
    public void MatchesPattern_HostCaseInsensitive_ReturnsTrue()
    {
        var info = new SshConnectionInfo { Host = "GitHub.COM", Repository = "owner/repo.git" };
        Assert.True(info.MatchesPattern("github.com:*"));
    }

    [Fact]
    public void MatchesPattern_NullHost_ReturnsFalse()
    {
        var info = new SshConnectionInfo { Host = null, Repository = "owner/repo.git" };
        Assert.False(info.MatchesPattern("github.com:*"));
    }

    #endregion

    #region MatchesPattern Tests - Owner Wildcard

    [Fact]
    public void MatchesPattern_OwnerWildcard_MatchingOwner_ReturnsTrue()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = "myorg/repo.git" };
        Assert.True(info.MatchesPattern("github.com:myorg/*"));
    }

    [Fact]
    public void MatchesPattern_OwnerWildcard_DifferentOwner_ReturnsFalse()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = "otherorg/repo.git" };
        Assert.False(info.MatchesPattern("github.com:myorg/*"));
    }

    [Fact]
    public void MatchesPattern_OwnerWildcard_CaseInsensitive_ReturnsTrue()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = "MyOrg/repo.git" };
        Assert.True(info.MatchesPattern("github.com:myorg/*"));
    }

    #endregion

    #region MatchesPattern Tests - Exact Match with .git suffix

    [Fact]
    public void MatchesPattern_ExactMatch_BothWithGitSuffix_ReturnsTrue()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = "owner/repo.git" };
        Assert.True(info.MatchesPattern("github.com:owner/repo.git"));
    }

    [Fact]
    public void MatchesPattern_ExactMatch_OnlyRepoWithGitSuffix_ReturnsTrue()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = "owner/repo.git" };
        Assert.True(info.MatchesPattern("github.com:owner/repo"));
    }

    [Fact]
    public void MatchesPattern_ExactMatch_OnlyPatternWithGitSuffix_ReturnsTrue()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = "owner/repo" };
        Assert.True(info.MatchesPattern("github.com:owner/repo.git"));
    }

    [Fact]
    public void MatchesPattern_ExactMatch_NeitherWithGitSuffix_ReturnsTrue()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = "owner/repo" };
        Assert.True(info.MatchesPattern("github.com:owner/repo"));
    }

    [Fact]
    public void MatchesPattern_ExactMatch_CaseInsensitive_ReturnsTrue()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = "Owner/Repo.git" };
        Assert.True(info.MatchesPattern("github.com:owner/repo"));
    }

    [Fact]
    public void MatchesPattern_ExactMatch_DifferentRepo_ReturnsFalse()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = "owner/repo.git" };
        Assert.False(info.MatchesPattern("github.com:owner/other"));
    }

    [Theory]
    [InlineData("owner/repo.GIT", "owner/repo")]     // .GIT suffix should be stripped (case insensitive)
    [InlineData("owner/repo.Git", "owner/repo")]     // .Git suffix should be stripped (case insensitive)
    public void MatchesPattern_GitSuffixHandling(string repository, string pattern)
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = repository };
        Assert.True(info.MatchesPattern($"github.com:{pattern}"));
    }

    [Fact]
    public void MatchesPattern_DifferentRepoNames_ReturnsFalse()
    {
        // "repogit" is NOT the same as "repo.git" with .git stripped
        var info = new SshConnectionInfo { Host = "github.com", Repository = "owner/repogit" };
        Assert.False(info.MatchesPattern("github.com:owner/repo"));
    }

    [Fact]
    public void MatchesPattern_GitSuffixBug_DoesNotStripRandomChars()
    {
        // This test verifies the bug fix: TrimEnd(".git".ToCharArray()) would strip any g, i, t chars
        // "repotig" would incorrectly match "repo" with the old buggy code
        var info = new SshConnectionInfo { Host = "github.com", Repository = "owner/repotig" };
        Assert.False(info.MatchesPattern("github.com:owner/repo"));
    }

    [Fact]
    public void MatchesPattern_GitSuffixBug_DoesNotStripGitInMiddle()
    {
        // "gitrepo.git" should match "gitrepo", not have "git" stripped from both ends
        var info = new SshConnectionInfo { Host = "github.com", Repository = "owner/gitrepo.git" };
        Assert.True(info.MatchesPattern("github.com:owner/gitrepo"));
    }

    #endregion

    #region MatchesPattern Tests - Edge Cases

    [Fact]
    public void MatchesPattern_EmptyPattern_ReturnsFalse()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = "owner/repo" };
        Assert.False(info.MatchesPattern(""));
    }

    [Fact]
    public void MatchesPattern_PatternWithoutColon_MatchesAnyPath()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = "owner/repo" };
        Assert.True(info.MatchesPattern("github.com"));
    }

    [Fact]
    public void MatchesPattern_NullRepository_ExactMatch_ReturnsFalse()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = null };
        Assert.False(info.MatchesPattern("github.com:owner/repo"));
    }

    [Fact]
    public void MatchesPattern_NullRepository_Wildcard_ReturnsTrue()
    {
        var info = new SshConnectionInfo { Host = "github.com", Repository = null };
        Assert.True(info.MatchesPattern("github.com:*"));
    }

    #endregion
}

public class ParseSshCommandLineTests
{
    #region Basic Parsing

    [Fact]
    public void ParseSshCommandLine_Null_ReturnsNull()
    {
        var result = ProcessHelper.ParseSshCommandLine(null);
        Assert.Null(result);
    }

    [Fact]
    public void ParseSshCommandLine_Empty_ReturnsNull()
    {
        var result = ProcessHelper.ParseSshCommandLine("");
        Assert.Null(result);
    }

    [Fact]
    public void ParseSshCommandLine_NoUserHost_ReturnsNull()
    {
        var result = ProcessHelper.ParseSshCommandLine("ssh -o Option=value");
        Assert.Null(result);
    }

    #endregion

    #region User@Host Extraction

    [Fact]
    public void ParseSshCommandLine_SimpleUserHost_ExtractsCorrectly()
    {
        var result = ProcessHelper.ParseSshCommandLine("ssh git@github.com");
        Assert.NotNull(result);
        Assert.Equal("git", result.User);
        Assert.Equal("github.com", result.Host);
    }

    [Fact]
    public void ParseSshCommandLine_WithOptions_ExtractsUserHost()
    {
        var result = ProcessHelper.ParseSshCommandLine("ssh -o StrictHostKeyChecking=no git@github.com");
        Assert.NotNull(result);
        Assert.Equal("git", result.User);
        Assert.Equal("github.com", result.Host);
    }

    [Fact]
    public void ParseSshCommandLine_QuotedPath_ExtractsUserHost()
    {
        var result = ProcessHelper.ParseSshCommandLine("\"C:\\Windows\\System32\\OpenSSH\\ssh.exe\" git@github.com");
        Assert.NotNull(result);
        Assert.Equal("git", result.User);
        Assert.Equal("github.com", result.Host);
    }

    #endregion

    #region Git Command Extraction

    [Fact]
    public void ParseSshCommandLine_GitUploadPack_ExtractsCommand()
    {
        var result = ProcessHelper.ParseSshCommandLine("ssh git@github.com git-upload-pack 'owner/repo.git'");
        Assert.NotNull(result);
        Assert.Equal("git-upload-pack", result.GitCommand);
    }

    [Fact]
    public void ParseSshCommandLine_GitReceivePack_ExtractsCommand()
    {
        var result = ProcessHelper.ParseSshCommandLine("ssh git@github.com git-receive-pack 'owner/repo.git'");
        Assert.NotNull(result);
        Assert.Equal("git-receive-pack", result.GitCommand);
    }

    [Fact]
    public void ParseSshCommandLine_NoGitCommand_CommandIsNull()
    {
        var result = ProcessHelper.ParseSshCommandLine("ssh git@github.com");
        Assert.NotNull(result);
        Assert.Null(result.GitCommand);
    }

    #endregion

    #region Repository Extraction

    [Fact]
    public void ParseSshCommandLine_QuotedRepo_ExtractsRepository()
    {
        var result = ProcessHelper.ParseSshCommandLine("ssh git@github.com git-upload-pack 'owner/repo.git'");
        Assert.NotNull(result);
        Assert.Equal("owner/repo.git", result.Repository);
    }

    [Fact]
    public void ParseSshCommandLine_UnquotedRepo_ExtractsRepository()
    {
        var result = ProcessHelper.ParseSshCommandLine("ssh git@github.com git-upload-pack owner/repo.git");
        Assert.NotNull(result);
        Assert.Equal("owner/repo.git", result.Repository);
    }

    [Fact]
    public void ParseSshCommandLine_RepoWithoutGitSuffix_ExtractsRepository()
    {
        var result = ProcessHelper.ParseSshCommandLine("ssh git@github.com git-upload-pack 'owner/repo'");
        Assert.NotNull(result);
        Assert.Equal("owner/repo", result.Repository);
    }

    [Fact]
    public void ParseSshCommandLine_ComplexPath_ExtractsRepository()
    {
        var result = ProcessHelper.ParseSshCommandLine("ssh git@github.com git-upload-pack 'organization/sub-project/repo.git'");
        Assert.NotNull(result);
        Assert.Equal("organization/sub-project/repo.git", result.Repository);
    }

    #endregion

    #region Full Command Line Examples

    [Fact]
    public void ParseSshCommandLine_RealGitCloneCommand_ParsesCorrectly()
    {
        var cmdLine = "\"C:\\Windows\\System32\\OpenSSH\\ssh.exe\" -o SendEnv=GIT_PROTOCOL git@github.com git-upload-pack 'jss826/SshAgentProxy.git'";
        var result = ProcessHelper.ParseSshCommandLine(cmdLine);

        Assert.NotNull(result);
        Assert.Equal("git", result.User);
        Assert.Equal("github.com", result.Host);
        Assert.Equal("git-upload-pack", result.GitCommand);
        Assert.Equal("jss826/SshAgentProxy.git", result.Repository);
        Assert.Equal("jss826", result.GetOwner());
    }

    [Fact]
    public void ParseSshCommandLine_GitPushCommand_ParsesCorrectly()
    {
        var cmdLine = "ssh git@github.com git-receive-pack 'myorg/myrepo.git'";
        var result = ProcessHelper.ParseSshCommandLine(cmdLine);

        Assert.NotNull(result);
        Assert.Equal("git", result.User);
        Assert.Equal("github.com", result.Host);
        Assert.Equal("git-receive-pack", result.GitCommand);
        Assert.Equal("myorg/myrepo.git", result.Repository);
    }

    #endregion
}
