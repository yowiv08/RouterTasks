#requires -Version 7
param(
    [string]$Tag,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/release')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $OutputEncoding

function Get-Sha256([string]$File) {
    (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256([string]$Value) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($Value))).ToLowerInvariant()
}

function Test-Inside([string]$Parent, [string]$Child) {
    $relative = [IO.Path]::GetRelativePath($Parent, $Child)
    $relative -eq '.' -or (
        -not [IO.Path]::IsPathRooted($relative) -and
        $relative -ne '..' -and
        -not $relative.StartsWith("..$([IO.Path]::DirectorySeparatorChar)")
    )
}

if ($Tag -and $Tag -cnotmatch '^v[0-9]+\.[0-9]+(?:\.[0-9]+)?$') {
    throw 'Tag 必须使用 v0.3 或 v0.3.1 格式。'
}

$root = [IO.Path]::GetFullPath($PSScriptRoot)
$projectVersion = ([xml](Get-Content -LiteralPath (Join-Path $root 'src/Plugins.RouterTasks/Plugins.RouterTasks.csproj') -Raw)).Project.PropertyGroup.Version
if ($projectVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw '项目版本必须使用 0.1.0 格式。' }
$versionTags = @("v$projectVersion")
if (([version]$projectVersion).Build -eq 0) {
    $versionTags = @("v$(([version]$projectVersion).ToString(2))") + $versionTags
}
if (-not $Tag) { $Tag = $versionTags[0] }
if ($Tag -cnotin $versionTags) { throw "Release tag $Tag 与项目版本 v$projectVersion 不一致。" }
$npm = Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json
$npmLock = Get-Content -LiteralPath (Join-Path $root 'package-lock.json') -Raw | ConvertFrom-Json -AsHashtable
if ($npm.version -cne $projectVersion -or $npmLock.version -cne $projectVersion -or $npmLock.packages[''].version -cne $projectVersion) {
    throw '项目版本与 npm 元数据、锁文件版本不一致。'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
$staging = [IO.Path]::GetFullPath((Join-Path $root ('artifacts/staging/' + [Guid]::NewGuid().ToString('N'))))
if ((Test-Inside $output $staging) -or (Test-Inside $staging $output)) {
    throw '发行输出目录不能与临时构建目录相互包含。'
}
if (Test-Path -LiteralPath $output) {
    $existing = Get-Item -LiteralPath $output -Force
    if (-not $existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '发行输出必须是普通目录。'
    }
    if (@(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) {
        throw "发行目录必须为空：$output"
    }
}

& (Join-Path $root 'build.ps1') -OutputDirectory $staging
if ($LASTEXITCODE -ne 0) { throw '插件编译失败。' }

$package = Join-Path $staging 'router-tasks'
$manifestPath = Join-Path $package 'plugin.json'
$assemblyPath = Join-Path $package 'Plugins.RouterTasks.dll'
if (-not (Test-Path -LiteralPath $manifestPath) -or -not (Test-Path -LiteralPath $assemblyPath)) {
    throw '缺少插件入口 DLL 或 plugin.json。'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version.ToString()
if ($manifest.id -cne 'router-tasks' -or $manifest.runtime -cne 'dotnet' -or
    $manifest.assembly -cne 'Plugins.RouterTasks.dll' -or $manifest.version -cne $assemblyVersion -or
    [string]::IsNullOrWhiteSpace($manifest.description)) {
    throw '插件元数据无效。'
}
$normalizedVersion = ([version]$assemblyVersion).ToString(3)
if ($normalizedVersion -cne $projectVersion) {
    throw "插件版本 v$normalizedVersion 与项目版本 v$projectVersion 不一致。"
}

$files = @{}
foreach ($item in Get-ChildItem -LiteralPath $package -Recurse -Force) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "插件目录中存在符号链接：$($item.FullName)"
    }
    if (-not $item.PSIsContainer) {
        $relative = [IO.Path]::GetRelativePath($package, $item.FullName).Replace('\', '/')
        $files[$relative] = $item
    }
}
if (-not $files.ContainsKey('plugin.json') -or -not $files.ContainsKey('Plugins.RouterTasks.dll')) {
    throw '发行目录缺少插件必需文件。'
}

$names = [string[]]@($files.Keys)
[Array]::Sort($names, [StringComparer]::Ordinal)
$fileHashes = @{}
$content = ($names | ForEach-Object {
        $fileHashes[$_] = Get-Sha256 $files[$_].FullName
        "$_ $($fileHashes[$_])"
    }) -join "`n"
$contentHash = Get-TextSha256 $content

New-Item -ItemType Directory -Force -Path $output | Out-Null
$asset = 'router-tasks.zip'
$archivePath = Join-Path $output $asset
Compress-Archive -LiteralPath $package -DestinationPath $archivePath

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $seen = @{}
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        $name = $entry.FullName.Replace('\', '/')
        if (-not $name.StartsWith('router-tasks/', [StringComparison]::Ordinal)) {
            throw 'ZIP 顶层目录无效。'
        }
        $relative = $name.Substring('router-tasks/'.Length)
        if (-not $fileHashes.ContainsKey($relative) -or $seen.ContainsKey($relative)) {
            throw 'ZIP 文件集合无效。'
        }
        $stream = $entry.Open()
        try {
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        } finally { $stream.Dispose() }
        if ($hash -cne $fileHashes[$relative]) { throw "ZIP 文件校验失败：$relative" }
        $seen[$relative] = $true
    }
    if ($seen.Count -ne $fileHashes.Count) { throw 'ZIP 缺少文件。' }
} finally { $archive.Dispose() }

$entry = [ordered]@{
    id = $manifest.id
    name = $manifest.name.Trim()
    description = $manifest.description.Trim()
    runtime = 'dotnet'
    version = $assemblyVersion
    asset = $asset
    sha256 = Get-Sha256 $archivePath
    contentSha256 = $contentHash
    sizeBytes = (Get-Item -LiteralPath $archivePath).Length
}
[ordered]@{ schemaVersion = 1; tag = $Tag; plugins = @($entry) } |
    ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $output 'release-index.json') -Encoding utf8NoBOM
"$($entry.sha256)  $asset" | Set-Content -LiteralPath (Join-Path $output "$asset.sha256") -Encoding ascii

$notes = @(
    "# RouterTasks $Tag"
    ''
    $entry.description
    ''
    "版本：$assemblyVersion · 运行时：.NET 10 · 下载：$asset"
) -join "`n"
$customNotes = Join-Path $root "release-notes/$Tag.md"
if (Test-Path -LiteralPath $customNotes -PathType Leaf) {
    $notes += "`n`n" + (Get-Content -LiteralPath $customNotes -Raw)
}
$notes | Set-Content -LiteralPath (Join-Path $output 'release-notes.md') -Encoding utf8NoBOM
Write-Output "发行目录：$output"
