#requires -Version 7
param(
    [switch]$Browser,
    [switch]$HostIntegration,
    [string]$RouterHostProject,
    [string]$PluginZip
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & dotnet test tests/RouterTasks.Tests/RouterTasks.Tests.csproj --disable-build-servers -m:1 -p:UseSharedCompilation=false --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw 'C# 测试失败。' }
    & npm run check
    if ($LASTEXITCODE -ne 0) { throw 'JS 语法检查失败。' }
    & npm test
    if ($LASTEXITCODE -ne 0) { throw '页面测试失败。' }
    if ($Browser) { & npm run test:browser; if ($LASTEXITCODE -ne 0) { throw '浏览器测试失败。' } }
    if ($HostIntegration) {
        if (-not $RouterHostProject) {
            $pointer = Join-Path $PSScriptRoot 'artifacts/host-source-path.txt'
            if (Test-Path -LiteralPath $pointer) {
                $RouterHostProject = Join-Path ((Get-Content -LiteralPath $pointer -Raw).Trim()) 'src/Router.Host/Router.Host.csproj'
            }
        }
        if (-not $RouterHostProject -or -not (Test-Path -LiteralPath $RouterHostProject -PathType Leaf)) {
            throw '宿主集成验收需要 -RouterHostProject 指定隔离宿主源码中的 Router.Host.csproj。'
        }
        $RouterHostProject = (Resolve-Path -LiteralPath $RouterHostProject).Path
        $run = Join-Path $PSScriptRoot ('artifacts/acceptance/' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
        if (-not $PluginZip) {
            & (Join-Path $PSScriptRoot 'package-release.ps1') -OutputDirectory (Join-Path $run 'release')
            $PluginZip = Join-Path $run 'release/router-tasks.zip'
        }
        if (-not (Test-Path -LiteralPath $PluginZip -PathType Leaf)) { throw '插件 ZIP 不存在。' }
        $PluginZip = (Resolve-Path -LiteralPath $PluginZip).Path
        & dotnet run --project tests/RouterTasks.HostHarness/RouterTasks.HostHarness.csproj -c Release `
            --disable-build-servers -p:UseSharedCompilation=false "-p:RouterHostProject=$RouterHostProject" `
            -- $PluginZip (Join-Path $run 'host')
        if ($LASTEXITCODE -ne 0) { throw '宿主加载、热更新与卸载验收失败。' }
        & dotnet run --project tests/RouterTasks.HostHarness/RouterTasks.HostHarness.csproj -c Release `
            --no-build "-p:RouterHostProject=$RouterHostProject" -- $PluginZip (Join-Path $run 'storage') --storage-only
        if ($LASTEXITCODE -ne 0) { throw '宿主持久化验收失败。' }
        Write-Output "宿主验收报告：$run"
    }
} finally { Pop-Location }
