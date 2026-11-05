param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $root
[xml]$appProject = Get-Content -LiteralPath 'src/SteamCardCollector.App/SteamCardCollector.App.csproj' -Raw
$appVersion = [string]$appProject.Project.PropertyGroup.Version
$runtimeVersion = [string]$appProject.Project.PropertyGroup.RuntimeFrameworkVersion
if ($appVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') { throw 'Invalid application version in the App csproj.' }
if ($runtimeVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid runtime version in the App csproj.' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = "dist/$appVersion" }
$release = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if (Test-Path -LiteralPath $release) { throw "Output already exists: $release. Choose a new -OutputDirectory." }

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
function Run-AppCheck([string]$Exe, [string]$Mode, [string]$Report) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Exe
    $info.Arguments = "$Mode `"$Report`""
    $info.WorkingDirectory = Split-Path -Parent $Exe
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (!$process.WaitForExit(30000)) { $process.Kill(); throw "$Mode timed out" }
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $Report) -or
            !(Get-Content -LiteralPath $Report -Raw).StartsWith('PASS:')) { throw "$Mode failed" }
        Get-Content -LiteralPath $Report
    }
    finally { $process.Dispose() }
}

Invoke-Dotnet @('build', 'SteamCardCollector.sln', '-c', 'Release')
Invoke-Dotnet @('run', '--project', 'tests/SteamCardCollector.Checks', '-c', 'Release', '--no-build')
$windows = Join-Path $release "SteamCardCollector-$appVersion-win-x64"
Invoke-Dotnet @('publish', 'src/SteamCardCollector.App', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $windows)

$nugetRoot = ((& dotnet nuget locals global-packages --list) -replace '^[^:]+:\s*', '').Trim()
$runtime = Join-Path $nugetRoot "microsoft.netcore.app.runtime.win-x64/$runtimeVersion"
Copy-Item -LiteralPath (Join-Path $runtime 'LICENSE.TXT') -Destination 'ThirdParty/dotnet-LICENSE.TXT' -Force
Copy-Item -LiteralPath (Join-Path $runtime 'THIRD-PARTY-NOTICES.TXT') -Destination 'ThirdParty/dotnet-THIRD-PARTY-NOTICES.TXT' -Force
Copy-Item -LiteralPath README.md, THIRD-PARTY-NOTICES.md -Destination $windows
Copy-Item -LiteralPath ThirdParty -Destination $windows -Recurse
$verification = Join-Path $release 'verification'
New-Item -ItemType Directory -Path $verification | Out-Null
$exe = Join-Path $windows 'SteamCardCollector.App.exe'
Run-AppCheck $exe '--native-self-test' (Join-Path $verification 'native.txt')
Run-AppCheck $exe '--ui-self-test' (Join-Path $verification 'ui.txt')

# Reject a guaranteed mismatched account before SteamAPI.Init, without a real login.
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = $exe
$info.Arguments = '--local-worker 10 1 1'
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($info)
try {
    $replyTask = $process.StandardOutput.ReadToEndAsync()
    $errorTask = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(15000)) { $process.Kill(); throw 'Worker rejection test timed out' }
    $reply = $replyTask.GetAwaiter().GetResult() | ConvertFrom-Json
    $null = $errorTask.GetAwaiter().GetResult()
    if ($process.ExitCode -eq 0 -or $null -eq $reply -or $reply.Ready -or $reply.SteamId -ne 1) { throw 'Worker accepted a mismatched account' }
    'PASS: mismatched worker rejected before game initialization.' | Set-Content (Join-Path $verification 'worker.txt')
}
finally { $process.Dispose() }

$source = Join-Path $release "SteamCardCollector-$appVersion-source"
New-Item -ItemType Directory -Path $source | Out-Null
Copy-Item -LiteralPath SteamCardCollector.sln, Directory.Build.props, README.md, THIRD-PARTY-NOTICES.md -Destination $source
if (Test-Path -LiteralPath '.gitignore') {
    Copy-Item -LiteralPath '.gitignore' -Destination $source
} else {
    # Browser uploads can omit dotfiles. Keep packaging usable and source caches excluded.
    @('**/bin/', '**/obj/', '.vs/', '.codex/', 'artifacts/', 'dist/', '*.user', '*.suo') |
        Set-Content -LiteralPath (Join-Path $source '.gitignore') -Encoding ascii
}
foreach ($directory in @('.github', 'src', 'tests', 'scripts', 'lib', 'ThirdParty')) {
    Get-ChildItem -LiteralPath (Join-Path $root $directory) -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object {
            $relative = $_.FullName.Substring($root.Length + 1)
            $destination = Join-Path $source $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
}
$windowsZip = Join-Path $release "SteamCardCollector-$appVersion-win-x64.zip"
$sourceZip = Join-Path $release "SteamCardCollector-$appVersion-source.zip"
Compress-Archive -LiteralPath $windows -DestinationPath $windowsZip -CompressionLevel Optimal
Compress-Archive -LiteralPath $source -DestinationPath $sourceZip -CompressionLevel Optimal
@($windowsZip, $sourceZip) | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
    '{0}  {1}' -f $hash.Hash, [IO.Path]::GetFileName($_)
} | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Write-Output "EXE: $exe"
Write-Output "Windows ZIP: $windowsZip"
Write-Output "Source ZIP: $sourceZip"
