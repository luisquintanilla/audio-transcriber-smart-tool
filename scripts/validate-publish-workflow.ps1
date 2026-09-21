[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $WorkflowPath = (Join-Path $PSScriptRoot '..\.github\workflows\publish-packages.yml')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $WorkflowPath -PathType Leaf)) {
    throw "Workflow file was not found: $WorkflowPath"
}

$workflow = Get-Content -LiteralPath $WorkflowPath -Raw

function Assert-Contains {
    param(
        [string] $Pattern,
        [string] $Description
    )

    if ($workflow -notmatch $Pattern) {
        throw "Workflow validation failed: $Description"
    }
}

function Assert-NotContains {
    param(
        [string] $Pattern,
        [string] $Description
    )

    if ($workflow -match $Pattern) {
        throw "Workflow validation failed: $Description"
    }
}

function Count-Literal {
    param([string] $Value)

    return ([regex]::Matches($workflow, [regex]::Escape($Value))).Count
}

Assert-Contains '(?m)^on:\s*$' 'the workflow has an on block'
Assert-Contains "(?m)^\s+push:\s*$" 'tag pushes are supported'
Assert-Contains "(?m)^\s+tags:\s*$" 'tag filtering is configured'
Assert-Contains "(?m)^\s+- 'v\*'\s*$" 'tag filtering starts with v'
Assert-Contains '(?m)^\s+workflow_dispatch:\s*$' 'manual dispatch is supported'
Assert-Contains '(?ms)workflow_dispatch:.*?inputs:.*?version:.*?required:\s*true.*?type:\s*string' 'manual dispatch requires a version input'
Assert-NotContains '(?m)^\s+pull_request:' 'pull requests do not publish packages'
Assert-NotContains '(?m)^\s+branches:' 'ordinary branch pushes do not publish packages'

Assert-Contains '(?ms)permissions:\s*contents:\s*read\s+packages:\s*write' 'permissions are limited to contents read and packages write'
Assert-Contains 'actions/checkout@v4' 'checkout uses v4'
Assert-Contains 'actions/setup-dotnet@v4' 'setup-dotnet uses v4'
Assert-Contains 'global-json-file:\s*global\.json' 'the pinned SDK is selected from global.json'
Assert-Contains 'fetch-depth:\s*0' 'tag ancestry can be checked'
Assert-Contains 'persist-credentials:\s*false' 'checkout credentials are not persisted'

Assert-Contains 'semver=' 'the release version is validated'
Assert-Contains 'GITHUB_REF_NAME' 'tag version input is derived from the ref'
Assert-Contains '\$\{tag#v\}' 'tag versions strip the leading v'
Assert-Contains 'git merge-base --is-ancestor' 'tag releases are restricted to default-branch history'
Assert-Contains 'GITHUB_REF_NAME.*DEFAULT_BRANCH|DEFAULT_BRANCH.*GITHUB_REF_NAME' 'manual releases are restricted to the default branch'
Assert-Contains 'PACKAGE_VERSION=' 'the validated version is passed through the job'

Assert-Contains 'dotnet restore AudioTranscriber\.sln --configfile NuGet\.config' 'restore uses the repository NuGet configuration'
Assert-Contains 'dotnet build AudioTranscriber\.sln.*--configuration Release.*--no-restore' 'Release build follows restore'
Assert-Contains 'dotnet test AudioTranscriber\.sln.*--configuration Release.*--no-restore.*--no-build' 'Release tests follow the build'
Assert-Contains '--no-build' 'packing does not rebuild'
Assert-Contains '--no-restore' 'packing does not restore'
Assert-Contains '-p:PackageVersion=' 'packing receives a central PackageVersion'
Assert-Contains '-p:Version=' 'packing receives a central Version'

$expectedProjects = @(
    'src/AudioTranscriber/AudioTranscriber.csproj',
    'src/AudioTranscriber.FoundryLocal/AudioTranscriber.FoundryLocal.csproj',
    'src/AudioTranscriber.Granite/AudioTranscriber.Granite.csproj',
    'src/audio-transcriber/audio-transcriber.csproj'
)
foreach ($project in $expectedProjects) {
    if ((Count-Literal $project) -ne 1) {
        throw "Workflow validation failed: expected exactly one explicit pack project '$project'."
    }
}
$expectedPackageIds = @(
    'AudioTranscriber',
    'AudioTranscriber.FoundryLocal',
    'AudioTranscriber.Granite',
    'audio-transcriber'
)
foreach ($packageId in $expectedPackageIds) {
    if ((Count-Literal "`"$packageId`"") -ne 1) {
        throw "Workflow validation failed: expected exactly one explicit package ID '$packageId'."
    }
}
if ((Count-Literal 'dotnet pack') -ne 1) {
    throw 'Workflow validation failed: packing must use one explicit loop over the four projects.'
}
Assert-NotContains 'TranscriptIngestion|TranscriptProcessing' 'non-packable projects are not package inputs'

Assert-Contains 'https://nuget\.pkg\.github\.com/luisquintanilla/index\.json' 'publishing targets the owner GitHub Packages feed'
Assert-Contains 'secrets\.GITHUB_TOKEN' 'publishing uses the automatic GitHub token'
Assert-Contains '--skip-duplicate' 'reruns tolerate immutable packages that already exist'
Assert-Contains '--no-symbols' 'symbol packages are not published'
Assert-NotContains 'nuget\.org' 'publishing does not target nuget.org'
Assert-Contains 'expected_packages=' 'the package output is checked against an explicit package set'
Assert-Contains 'find "\$package_dir" -mindepth 1 -maxdepth 1 -type d' 'nested package output is rejected'

if (($workflow -split '\r?\n') | Where-Object { $_ -match "^\t" }) {
    throw 'Workflow validation failed: YAML indentation must use spaces, not tabs.'
}

Write-Output "Validated release workflow: $WorkflowPath"
