#requires -Version 7
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/package')
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$package = Join-Path $output 'router-tasks'
if ((Test-Path -LiteralPath $package) -and @(Get-ChildItem -LiteralPath $package -Force).Count) {
    throw "输出目录必须为空：$package"
}
$temporary = Join-Path $PSScriptRoot ('artifacts/publish/' + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $PSScriptRoot 'src/Plugins.RouterTasks/Plugins.RouterTasks.csproj'
& dotnet publish $project -c $Configuration --no-self-contained -o $temporary --disable-build-servers -m:1 -p:UseSharedCompilation=false -p:CopyLocalLockFileAssemblies=true
if ($LASTEXITCODE -ne 0) { throw '插件编译失败。' }
New-Item -ItemType Directory -Force -Path $package | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $temporary -Recurse -File) {
    if ($file.Name -like 'Router.Contracts.*' -or $file.Extension -eq '.pdb') { continue }
    $destination = Join-Path $package ([IO.Path]::GetRelativePath($temporary, $file.FullName))
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
$assembly = Join-Path $package 'Plugins.RouterTasks.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw '缺少插件入口 DLL。' }
$description = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Description
[ordered]@{
    schemaVersion = 1
    id = 'router-tasks'
    name = 'RouterTasks'
    description = $description
    runtime = 'dotnet'
    assembly = 'Plugins.RouterTasks.dll'
    version = [Reflection.AssemblyName]::GetAssemblyName($assembly).Version.ToString()
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'plugin.json') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $package
New-Item -ItemType Directory -Force -Path (Join-Path $package 'docs') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/API.md') -Destination (Join-Path $package 'docs/API.md')
Write-Output "插件目录：$package"
