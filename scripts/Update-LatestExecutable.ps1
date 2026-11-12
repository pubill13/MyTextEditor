param(
    [string]$SourceExecutable
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'src/MyTextEditor/MyTextEditor.csproj'
$latestDirectory = Join-Path $projectRoot 'artifacts/latest'
$latestExecutable = Join-Path $latestDirectory 'OmniEdit.exe'

if (!$SourceExecutable) {
    $publishDirectory = Join-Path $projectRoot 'artifacts/latest-build/publish'
    dotnet publish $projectFile -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:NuGetAudit=false -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Latest executable publish failed; the existing fixed executable was preserved.' }
    $SourceExecutable = Join-Path $publishDirectory 'OmniEdit.exe'
}

$source = (Resolve-Path -LiteralPath $SourceExecutable).Path
[xml]$project = Get-Content -Raw -LiteralPath $projectFile
$expectedVersion = @($project.Project.PropertyGroup.FileVersion | Where-Object { $_ })[0]
$actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($source).FileVersion
if ($actualVersion -ne $expectedVersion) { throw "Executable version mismatch: expected $expectedVersion, found $actualVersion" }
$expectedHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
[IO.Directory]::CreateDirectory($latestDirectory) | Out-Null

if (![StringComparer]::OrdinalIgnoreCase.Equals($source, $latestExecutable)) {
    $temporary = Join-Path $latestDirectory ('OmniEdit-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::Copy($source, $temporary)
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Copied executable hash mismatch.' }
        # Stage the complete file before replacing the fixed path; a locked destination stays intact.
        if ([IO.File]::Exists($latestExecutable)) { [IO.File]::Replace($temporary, $latestExecutable, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $latestExecutable) }
    }
    catch [IO.IOException] {
        throw [IO.IOException]::new('Cannot update artifacts/latest/OmniEdit.exe. If it is running, close its main and comparison windows and retry. The update was not completed.', $_.Exception)
    }
    finally {
        if ([IO.File]::Exists($temporary)) { Remove-Item -LiteralPath $temporary }
    }
}

if ((Get-FileHash -LiteralPath $latestExecutable -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Fixed executable hash mismatch.' }
[pscustomobject]@{ Executable=$latestExecutable; Version=$actualVersion; SHA256=$expectedHash }
