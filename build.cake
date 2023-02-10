#l docs.cake
#tool "nuget:?package=xunit.runner.console&version=2.4.1"
#tool "nuget:?package=GitVersion.CommandLine&version=5.0.1"
#tool "nuget:?package=gitreleasemanager&version=0.13.0"
#tool "nuget:?package=gitlink&version=2.4.0"
#addin "nuget:?package=Cake.Incubator&version=5.1.0"

///////////////////////////////////////////////////////////////////////////////
// ARGUMENTS
///////////////////////////////////////////////////////////////////////////////
var envBuildNumber = EnvironmentVariable<int>("APPVEYOR_BUILD_NUMBER", 0);
var gitHubToken = EnvironmentVariable("GITHUB_TOKEN");
var nugetSourceUrl = EnvironmentVariable("NUGET_SOURCE");
var nugetApiKey = EnvironmentVariable("NUGET_API_KEY");

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var buildNumber = Argument<int>("buildNumber", envBuildNumber);

///////////////////////////////////////////////////////////////////////////////
// VARIABLES
///////////////////////////////////////////////////////////////////////////////

// folders
var artifactsDir        = Directory("./artifacts");
var nugetPackageDir     = artifactsDir + Directory("nuget-packages");
var srcDir              = Directory("./src");
var rootPath            = MakeAbsolute(Directory("./"));
var releaseNotesPath = rootPath.CombineWithFilePath("CHANGELOG.md");

// project specific
var solutionFile        = srcDir + File("ServiceStack.Quartz.sln");
var gitHubRepositoryOwner = "czesiu";
var gitHubRepositoryName = "ServiceStack.Quartz2";

var isLocalBuild = BuildSystem.IsLocalBuild;
var isPullRequest = BuildSystem.AppVeyor.Environment.PullRequest.IsPullRequest;
var isMasterBranch = BuildSystem.AppVeyor.Environment.Repository.Branch.EqualsIgnoreCase("master");
var isReleaseBranch = BuildSystem.AppVeyor.Environment.Repository.Branch.StartsWithIgnoreCase("release");
var isHotFixBranch = BuildSystem.AppVeyor.Environment.Repository.Branch.StartsWithIgnoreCase("hotfix");
var isTagged = BuildSystem.AppVeyor.Environment.Repository.Tag.IsTag && !BuildSystem.AppVeyor.Environment.Repository.Tag.Name.IsNullOrEmpty();
var publishingError = false;

var shouldPublishNuGet = !isLocalBuild && !isPullRequest && isMasterBranch;
var shouldPublishGitHub = shouldPublishNuGet;

var gitVersionResults   = GitVersion(new GitVersionSettings { UpdateAssemblyInfo = false });
var assemblySemVersion  = $"{gitVersionResults.MajorMinorPatch}.{gitVersionResults.CommitsSinceVersionSource}";

if (BuildSystem.AppVeyor.IsRunningOnAppVeyor)
{
    Information("Branch -> {0}", BuildSystem.AppVeyor.Environment.Repository.Branch);
    Information("IsTag -> {0}", BuildSystem.AppVeyor.Environment.Repository.Tag.IsTag);
    Information("Tag -> {0}", BuildSystem.AppVeyor.Environment.Repository.Tag.Name);
}
else
{
    Information("Not running on AppVeyor");
}

Information("AssemblySemVersion -> {0}", assemblySemVersion);
Information("NuGetVersion -> {0}", gitVersionResults.NuGetVersion);
Information("ShouldPublishNuGet -> {0}", shouldPublishNuGet);

var projects = ParseSolution(solutionFile).GetProjects().Select(x => ParseProject(x.Path, configuration));

///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup(ctx =>
{
    // Executed BEFORE the first task.
    Information("Running tasks...");

    if(isMasterBranch && (ctx.Log.Verbosity != Verbosity.Diagnostic)) {
        Information("Increasing verbosity to diagnostic.");
        ctx.Log.Verbosity = Verbosity.Diagnostic;
    }
});

Teardown(ctx =>
{
   // Executed AFTER the last task.
   Information("Finished running tasks.");
});

///////////////////////////////////////////////////////////////////////////////
// TASKS
///////////////////////////////////////////////////////////////////////////////

Task("Default")
.IsDependentOn("Clean")
.IsDependentOn("Build")
.IsDependentOn("Test");

Task("Build")
.Does(() => {
    Information("Building {0}", solutionFile);
    var msbuildBinaryLogFile = artifactsDir + new FilePath(solutionFile.Path.GetFilenameWithoutExtension() + ".binlog");

    MSBuild(solutionFile.Path, settings => {
        settings
            .SetConfiguration(configuration)
            .SetMaxCpuCount(0) // use as many cpu's as are available
            .WithRestore()
            .WithProperty("TreatresultsAsErrors", "false")
            .WithProperty("resultsAsErrors", "3884")
            .WithProperty("CodeContractsRunCodeAnalysis", "true")
            .WithProperty("RunCodeAnalysis", "false")
            .WithProperty("Version", assemblySemVersion)
            .WithProperty("PackageVersion", gitVersionResults.NuGetVersion)
            .WithProperty("PackageOutputPath", MakeAbsolute(nugetPackageDir).FullPath)
            .SetNodeReuse(false);

            // setup binary logging for solution to artifacts dir
            settings.ArgumentCustomization = arguments => {
                arguments.Append(string.Format("/bl:{0}", msbuildBinaryLogFile));
                return arguments;
            };
    });
});

Task("Test")
.Does(() => {
    Information("Testing for {0}", solutionFile);
    var testProjects = projects.Where(x => x.IsTestProject());
    foreach(var proj in testProjects){
        DotNetCoreTest(proj.ProjectFilePath.FullPath);
    }
});

Task("ReleaseNotes")
.IsDependentOn("Create-Release-Notes");

Task("AppVeyor")
    .IsDependentOn("Default")
    .IsDependentOn("Upload-AppVeyor-Artifacts")
    .IsDependentOn("Publish-Nuget-Packages")
    .IsDependentOn("Publish-GitHub-Release")
    .Finally(() =>
{
    if(publishingError)
    {
        throw new Exception($"An error occurred during the publishing of {solutionFile.Path}.  All publishing tasks have been attempted.");
    }
});

Task("Create-Release-Notes")
.Does(() => {
    Information("Creating release notes for {0}", assemblySemVersion);
    gitHubToken.ThrowIfNull(nameof(gitHubToken));
    GitReleaseManagerCreate(gitHubToken, gitHubRepositoryOwner, gitHubRepositoryName, new GitReleaseManagerCreateSettings {
                Milestone         = gitVersionResults.MajorMinorPatch,
                Name              = gitVersionResults.MajorMinorPatch,
                Prerelease        = false,
                TargetCommitish   = "master",
            });
});

Task("Export-Release-Notes")
    .WithCriteria(() => shouldPublishGitHub)
.Does(() => {
    Information("Exporting release notes for {0}", solutionFile);
    gitHubToken.ThrowIfNull(nameof(gitHubToken));

    GitReleaseManagerExport(gitHubToken, gitHubRepositoryOwner, gitHubRepositoryName, releaseNotesPath, 
    new GitReleaseManagerExportSettings {
        TagName = gitVersionResults.MajorMinorPatch
    });
});

Task("Publish-GitHub-Release")
.IsDependentOn("Export-Release-Notes")
.WithCriteria(() => shouldPublishGitHub)
.Does(() => {
    Information("Publishing github release for {0}", solutionFile);
    gitHubToken.ThrowIfNull(nameof(gitHubToken));

    // upload packages as assets
    foreach(var package in GetFiles(nugetPackageDir.Path + "/*"))
    {
        GitReleaseManagerAddAssets(gitHubToken, gitHubRepositoryOwner, gitHubRepositoryName, gitVersionResults.MajorMinorPatch, package.ToString());
    }

    // close the release
    GitReleaseManagerClose(gitHubToken, gitHubRepositoryOwner, gitHubRepositoryName, gitVersionResults.MajorMinorPatch);
});

Task("Publish-Nuget-Packages")
.WithCriteria(() => shouldPublishNuGet)
.WithCriteria(() => DirectoryExists(nugetPackageDir))
.Does(() => {

    Information("Publishing NuGet Packages for {0}", solutionFile);

    nugetSourceUrl.ThrowIfNull(nameof(nugetSourceUrl));
    nugetApiKey.ThrowIfNull(nameof(nugetApiKey));
    var nupkgFiles = GetFiles(nugetPackageDir.Path + "/**/*.nupkg");

    foreach(var nupkgFile in nupkgFiles)
    {
        // Push the package.
        NuGetPush(nupkgFile, new NuGetPushSettings {
            Source = nugetSourceUrl,
            ApiKey = nugetApiKey,

        });
    }
});


Task("Upload-AppVeyor-Artifacts")
.IsDependentOn("Export-Release-Notes")
.WithCriteria(() => BuildSystem.IsRunningOnAppVeyor)
.WithCriteria(() => DirectoryExists(nugetPackageDir))
.Does(() => {
    Information("Uploading AppVeyor artifacts for {0}", solutionFile);
    foreach(var package in GetFiles(nugetPackageDir.Path + "/*"))
    {
        AppVeyor.UploadArtifact(package);
    }
});

Task("Sample")
.Does(() => {
    Information("Restoring NuGet Packages for {0}", solutionFile);
});

Task("Clean")
.Does(() => {
   CleanDirectories(new DirectoryPath[] {
        artifactsDir,
        nugetPackageDir
  	});
});

RunTarget(target);