using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;

using Serilog;

using System;
using System.Linq;
using Nuke.Common.Git;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

[ShutdownDotNetAfterServerBuild]
partial class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main() => Execute<Build>(x => x.Push);

    
    const string DevelopBranch = "dev";
    const string PreviewBranch = "preview";
    const string MainBranch = "main";
    
    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")] readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Parameter("Api key to push packages to nuget.org.")]
    [Secret]
    string NuGetApiKey;

    [Parameter("Api key to push packages to myget.org.")]
    [Secret]
    string MyGetApiKey;

    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository Repository;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath OutputDirectory => RootDirectory / "output";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    protected override void OnBuildInitialized()
    {
        base.OnBuildInitialized();
        NuGetApiKey ??= Environment.GetEnvironmentVariable(nameof(NuGetApiKey));
        MyGetApiKey ??= Environment.GetEnvironmentVariable(nameof(MyGetApiKey));
    }

    Target Initial => _ => _
        .Description("Initial")
        .OnlyWhenStatic(() => IsServerBuild)
        .Executes(() =>
        {
        });

    Target Clean => _ => _
        .Description("Clean Solution")
        .DependsOn(Initial)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(x=>x.DeleteDirectory());
            OutputDirectory.CreateOrCleanDirectory();
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .Description("Restore Solution")
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
            
            // MSBuild(s => s
            //     .SetTargetPath(Solution)
            //     .SetTargets("Restore"));
        });

    Target Compile => _ => _
        .Description("Compile Solution")
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .SetVersionPrefix(GetVersionPrefix())
                .SetVersionSuffix(GetVersionSuffix())
                .EnableContinuousIntegrationBuild()
                .EnableNoRestore());
            
            // MSBuild(s => s
            //     .SetTargetPath(Solution)
            //     .SetTargets("Rebuild")
            //     .SetConfiguration(Configuration)
            //     .SetMaxCpuCount(Environment.ProcessorCount)
            //     .SetNodeReuse(IsLocalBuild));
        });

    string GetVersionPrefix()
    {
        var dt = DateTimeNow();
        return $"{dt:yyyy}.{(dt.Month - 1) / 3 + 1}{dt:MM}.{dt:dd}";
    }

    string GetVersionSuffix()
    {
        return Repository.Branch?.ToLower() switch
        {
            PreviewBranch => $"{PreviewBranch}{DateTimeNow():HHmmss}",
            _ => null
        };
    }

    static DateTimeOffset DateTimeNow()
    {
        return DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
    }

    Target Copy => _ => _
        .Description("Copy NuGet Package")
        .OnlyWhenStatic(() => IsServerBuild && Configuration.Equals(Configuration.Release))
        .DependsOn(Compile)
        .Executes(() =>
        {
            OutputDirectory.GlobFiles("**/*.nupkg")
                ?.Where(x => !x.Extension.Equals(".symbols.nupkg"))
                .ForEach(x => CopyFileToDirectory(x, ArtifactsDirectory / "packages", FileExistsPolicy.OverwriteIfNewer));
        });

    Target Artifacts => _ => _
        .DependsOn(Copy)
        .OnlyWhenStatic(() => IsServerBuild)
        .Description("Upload Artifacts")
        .Produces(ArtifactsDirectory / "*.nupkg")
        .Executes(() =>
        {
            //Log.Information("Upload artifacts to azure...");
            //AzurePipelines
            //    .UploadArtifacts("artifacts", "artifacts", ArtifactsDirectory);
            //Log.Information("Upload artifacts to azure finished.");
        });
    
    Target Push => _ => _
        .Description("Push NuGet Package")
        .OnlyWhenStatic(() => IsServerBuild && Configuration.Equals(Configuration.Release))
        .DependsOn(Copy)
        .Requires(() => NuGetApiKey)
        .Requires(() => MyGetApiKey)
        .Executes(() =>
        {
            (ArtifactsDirectory / "packages").GlobFiles("**/*.nupkg")
                ?.Where(x => !x.Extension.EndsWith(".symbols.nupkg"))
                .ForEach(x=>Nuget(x));
        });

    Target Deploy => _ => _
        .Description("Deploy")
        .DependsOn(Push, Artifacts)
        .Executes(() =>
        {
            Log.Information("Deployed");
        });

    void Nuget(string x)
    {
        Nuget(x, "https://www.myget.org/F/godsharp/api/v2/package", MyGetApiKey);
        Nuget(x, "https://api.nuget.org/v3/index.json", NuGetApiKey);
    }

    void Nuget(string x, string source, string key) =>
        DotNetNuGetPush(s => s
            .SetTargetPath(x)
            .SetSource(source)
            .SetApiKey(key)
            .EnableSkipDuplicate()
        );
}