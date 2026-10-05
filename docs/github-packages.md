# Install and publish with GitHub Packages

The FYIsoft NuGet feed is `https://nuget.pkg.github.com/FYIsoft/index.json`.
GitHub Packages requires authentication even for public NuGet packages. A NuGet.org account
is not required. The SDKs target .NET 10.

## Install in your application

Create a GitHub **personal access token (classic)** with `read:packages` at
[GitHub token settings](https://github.com/settings/tokens). The account must have access to
the packages; authorize the token for the organization if it requires SSO.

Add the following `nuget.config` beside your application's solution. It contains no credentials.
Source mapping sends only FYIsoft packages to GitHub and resolves other dependencies on NuGet.org.

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="github" value="https://nuget.pkg.github.com/FYIsoft/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="github"><package pattern="FYIsoft.Extensions.AI.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

In PowerShell 7, set the credential for the current session without placing the token in a file:

```powershell
$githubUser = Read-Host 'GitHub username'
$githubToken = Read-Host 'GitHub classic token with read:packages' -MaskInput
$env:NuGetPackageSourceCredentials_github = "Username=$githubUser;Password=$githubToken;ValidAuthenticationTypes=Basic"
Remove-Variable githubToken

dotnet add package FYIsoft.Extensions.AI.Anthropic --version 0.6.0-preview
dotnet add package FYIsoft.Extensions.AI.Jev --version 0.1.0-preview
```

Run those commands in the application project directory, or supply your project path. You can
install either package independently. This session variable also works with `dotnet restore`;
an IDE launched separately needs its own authenticated NuGet configuration. On Windows,
NuGet can store encrypted credentials in the user's NuGet.Config outside the repository.

See the [Anthropic guide](../src/FYIsoft.Extensions.AI.Anthropic/README.md),
[Jev guide](../src/FYIsoft.Extensions.AI.Jev/README.md), and
[combined sample](../samples/SupportDeskSample/README.md) for usage.

## Publish a release

1. Update each changed project's Version and merge the release into `main`.
2. Open this repository's **Actions → Publish GitHub Packages → Run workflow**, selecting `main`.
3. The workflow builds, runs offline tests, packs, verifies package contents, and publishes both
   SDKs. Existing versions are skipped; release a new version to change a published package.
4. It restores both packages from GitHub into an empty package cache, then runs a synthetic
   consumer to verify installation and execution. The workflow artifacts contain packages,
   a SHA-256/source-commit manifest, and test results.

The workflow uses the repository's short-lived `GITHUB_TOKEN` with `packages: write`; no NuGet
API key or provider credentials are required. Only `main` can publish, and concurrent publishes
are serialized. Provider live tests are deliberately excluded from publication.

Packages are linked to this repository by RepositoryUrl. GitHub initially creates packages as
private. Administrators can manage visibility, inherited access, and access for other Actions
repositories from each package's settings. For another application's GitHub Actions workflow,
grant that repository package read access and use its `GITHUB_TOKEN` with `packages: read`.

References: [GitHub NuGet registry](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry),
[package permissions](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility),
[NuGet feed credentials](https://learn.microsoft.com/en-us/nuget/consume-packages/consuming-packages-authenticated-feeds).
