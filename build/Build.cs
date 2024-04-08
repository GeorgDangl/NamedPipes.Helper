using Nuke.Common;
using Nuke.Common.CI.Jenkins;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.AzureKeyVault;
using Nuke.Common.Tools.Coverlet;
using Nuke.Common.Tools.DocFX;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.ReportGenerator;
using Nuke.Common.Tools.Teams;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Nuke.GitHub;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Xml.XPath;
using static Nuke.Common.ChangeLog.ChangelogTasks;
using static Nuke.Common.IO.XmlTasks;
using static Nuke.Common.Tools.DocFX.DocFXTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Npm.NpmTasks;
using static Nuke.Common.Tools.ReportGenerator.ReportGeneratorTasks;
using static Nuke.GitHub.ChangeLogExtensions;
using static Nuke.GitHub.GitHubTasks;

class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [AzureKeyVaultConfiguration(
        BaseUrlParameterName = nameof(KeyVaultBaseUrl),
        ClientIdParameterName = nameof(KeyVaultClientId),
        ClientSecretParameterName = nameof(KeyVaultClientSecret),
        TenantIdParameterName = nameof(KeyVaultTenantId))]
    readonly AzureKeyVaultConfiguration KeyVaultSettings;

    [AzureKeyVault] readonly AzureKeyVault KeyVault;

    [Parameter] readonly string KeyVaultBaseUrl;
    [Parameter] readonly string KeyVaultClientId;
    [Parameter] readonly string KeyVaultClientSecret;
    [Parameter] readonly string KeyVaultTenantId;

    [GitVersion(Framework = "net6.0")] readonly GitVersion GitVersion;
    [GitRepository] readonly GitRepository GitRepository;

    string ChangeLogFile => RootDirectory / "CHANGELOG.md";
    AbsolutePath OutputDirectory => RootDirectory / "output";

    [AzureKeyVaultSecret] readonly string DanglPublicFeedSource;
    [AzureKeyVaultSecret] readonly string FeedzAccessToken;
    [AzureKeyVaultSecret] readonly string NuGetApiKey;
    [AzureKeyVaultSecret] readonly string GitHubAuthenticationToken;
    [AzureKeyVaultSecret] readonly string DanglCiCdTeamsWebhookUrl;

    void SendTeamsMessage(string title, string message, bool isError)
    {
        if (!string.IsNullOrWhiteSpace(DanglCiCdTeamsWebhookUrl))
        {
            var themeColor = isError ? "f44336" : "00acc1";
            TeamsTasks
                .SendTeamsMessage(m => m
                    .SetTitle(title)
                    .SetText(message)
                    .SetThemeColor(themeColor),
                    DanglCiCdTeamsWebhookUrl);
        }
    }

    protected override void OnTargetFailed(string target)
    {
        if (IsServerBuild)
        {
            SendTeamsMessage("Build Failed", $"Target {target} failed for Dangl.NamedPipes.Helper, " +
                        $"Branch: {GitRepository.Branch}", true);
        }
    }

    Target Clean => _ => _
        .Executes(() =>
        {
            (RootDirectory / "NamedPipes.Helper").GlobDirectories("**/bin", "**/obj").ForEach(d => d.DeleteDirectory());
            (RootDirectory / "test").GlobDirectories("**/bin", "**/obj").ForEach(d => d.DeleteDirectory());
            OutputDirectory.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetRestore();
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(x => x
                .SetConfiguration(Configuration)
                .EnableNoRestore()
                .SetProcessArgumentConfigurator(a => a.Add("-nodereuse:false"))
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetAssemblyVersion($"{GitVersion.Major}.{GitVersion.Minor}.{GitVersion.Patch}.0")
                .SetInformationalVersion(GitVersion.InformationalVersion));
        });

    private Target Pack => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var changeLog = GetCompleteChangeLog(ChangeLogFile)
                .EscapeStringPropertyForMsBuild();

            DotNetPack(x => x.SetConfiguration(Configuration)
                .SetPackageReleaseNotes(changeLog)
                .EnableNoBuild()
                .SetOutputDirectory(OutputDirectory)
                .SetVersion(GitVersion.NuGetVersion));
        });



    Target Push => _ => _
        .DependsOn(Pack)
        .Requires(() => DanglPublicFeedSource)
        .Requires(() => FeedzAccessToken)
        .Requires(() => NuGetApiKey)
        .Requires(() => Configuration == Configuration.Release)
        .OnlyWhenDynamic(() => Jenkins.Instance == null
            || Jenkins.Instance.ChangeId == null)
        .Executes(() =>
        {
            var packages = OutputDirectory.GlobFiles("*.nupkg")
                .Select(p => p.ToString())
                .Where(x => !x.EndsWith("symbols.nupkg"))
                .ToList();
            Assert.NotEmpty(packages);
            packages
                .ForEach(x =>
                {
                    DotNetNuGetPush(s => s
                        .SetTargetPath(x)
                        .SetSource(DanglPublicFeedSource)
                        .SetApiKey(FeedzAccessToken));

                    if (GitVersion.BranchName.Equals("main") || GitVersion.BranchName.Equals("origin/main"))
                    {
                        // Stable releases are published to NuGet
                        DotNetNuGetPush(s => s
                            .SetTargetPath(x)
                            .SetSource("https://api.nuget.org/v3/index.json")
                            .SetApiKey(NuGetApiKey));
                    }
                });

            SendTeamsMessage("New Release", $"New release available for Dangl.NamedPipes.Helper: {GitVersion.NuGetVersion}", false);
        });

    Target PublishGitHubRelease => _ => _
        .DependsOn(Pack)
        .Requires(() => GitHubAuthenticationToken)
        .OnlyWhenDynamic(() => GitVersion.BranchName.Equals("main") || GitVersion.BranchName.Equals("origin/main"))
        .Executes(async () =>
        {
            var releaseTag = $"v{GitVersion.MajorMinorPatch}";

            var changeLogSectionEntries = ExtractChangelogSectionNotes(ChangeLogFile);
            var latestChangeLog = changeLogSectionEntries
                .Aggregate((c, n) => c + Environment.NewLine + n);
            var completeChangeLog = $"## {releaseTag}" + Environment.NewLine + latestChangeLog;

            var repositoryInfo = GetGitHubRepositoryInfo(GitRepository);
            var nuGetPackages = OutputDirectory.GlobFiles("*.nupkg").Select(f => f.ToString()).ToArray();
            Assert.NotEmpty(nuGetPackages);

            await PublishRelease(x => x
                    .SetArtifactPaths(nuGetPackages)
                    .SetCommitSha(GitVersion.Sha)
                    .SetReleaseNotes(completeChangeLog)
                    .SetRepositoryName(repositoryInfo.repositoryName)
                    .SetRepositoryOwner(repositoryInfo.gitHubOwner)
                    .SetTag(releaseTag)
                    .SetToken(GitHubAuthenticationToken));
        });
}
