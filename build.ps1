<#
.SYNOPSIS
    编译 Local Models 的全部可执行文件。

.DESCRIPTION
    用 Windows 自带的 csc.exe（.NET Framework 4.x）编译，不需要 Visual Studio，
    也不需要 .NET SDK。

    四个目标：
      launcher     启动器        Local Models.exe
      installer    安装程序      Bonsai2-Setup.exe
      uninstaller  卸载程序      Uninstall.exe
      updater      增量更新包    LocalModels-Update.exe
      all          全部（默认）—— 按正确顺序编译

    注意：编译器只支持 C# 5 语法。字符串插值、out var、表达式体成员都会报错。

.PARAMETER Target
    要编译的目标：launcher / installer / uninstaller / updater / all。默认 all。

.PARAMETER SourceDir
    源码目录，默认 <仓库根>\src

.PARAMETER OutDir
    输出目录，默认 <仓库根>\bin

.PARAMETER WebUiDir
    网页源码目录（index.html 所在），默认 <仓库根>\webui

.PARAMETER Configuration
    保留参数，目前只做 Release 优化编译。

.EXAMPLE
    .\build.ps1
    编译全部四个。

.EXAMPLE
    .\build.ps1 -Target launcher
    只编译启动器。

.EXAMPLE
    .\build.ps1 -Target all -OutDir D:\out
    全部编译并输出到 D:\out
#>

[CmdletBinding()]
param(
    [ValidateSet('launcher', 'installer', 'uninstaller', 'updater', 'all')]
    [string] $Target = 'all',

    [string] $SourceDir,
    [string] $OutDir,
    [string] $WebUiDir,
    [string] $ChangeLogPath,

    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ─────────────────────────────────────────────────────────────
#  彩色输出
# ─────────────────────────────────────────────────────────────

$script:StepNo  = 0
$script:Failed  = 0

function Write-Head {
    param([string] $Text)
    Write-Host ''
    Write-Host ('═' * 62) -ForegroundColor DarkCyan
    Write-Host "  $Text" -ForegroundColor Cyan
    Write-Host ('═' * 62) -ForegroundColor DarkCyan
    Write-Host ''
}

function Write-Step {
    param([string] $Text)
    $script:StepNo++
    Write-Host ("  [{0}] " -f $script:StepNo) -ForegroundColor DarkGray -NoNewline
    Write-Host $Text -ForegroundColor White
}

function Write-Ok {
    param([string] $Text)
    Write-Host '      ✓ ' -ForegroundColor Green -NoNewline
    Write-Host $Text -ForegroundColor Gray
}

function Write-Info {
    param([string] $Text)
    Write-Host '      · ' -ForegroundColor DarkGray -NoNewline
    Write-Host $Text -ForegroundColor DarkGray
}

function Write-Warn {
    param([string] $Text)
    Write-Host '      ! ' -ForegroundColor Yellow -NoNewline
    Write-Host $Text -ForegroundColor Yellow
}

function Write-Fail {
    param([string] $Text)
    $script:Failed++
    Write-Host '      ✗ ' -ForegroundColor Red -NoNewline
    Write-Host $Text -ForegroundColor Red
}

# ─────────────────────────────────────────────────────────────
#  路径准备
# ─────────────────────────────────────────────────────────────

$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) {
    $root = Split-Path -Parent $MyInvocation.MyCommand.Definition
}

if ([string]::IsNullOrWhiteSpace($SourceDir)) { $SourceDir = Join-Path $root 'src' }
if ([string]::IsNullOrWhiteSpace($OutDir))    { $OutDir    = Join-Path $root 'bin' }

# 网页源码目录：仓库里叫 web\，构建工作区里叫 网页源码\ —— 两个名字都认
if ([string]::IsNullOrWhiteSpace($WebUiDir)) {
    foreach ($cand in @('web', 'webui', '网页源码')) {
        $p = Join-Path $root $cand
        if (Test-Path (Join-Path $p 'index.html')) { $WebUiDir = $p; break }
    }
    if ([string]::IsNullOrWhiteSpace($WebUiDir)) { $WebUiDir = Join-Path $root 'web' }
}

$SourceDir = [IO.Path]::GetFullPath($SourceDir)
$OutDir    = [IO.Path]::GetFullPath($OutDir)
$WebUiDir  = [IO.Path]::GetFullPath($WebUiDir)

$Csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

$Icon     = Join-Path $SourceDir 'bonsai.ico'
$Html     = Join-Path $WebUiDir  'index.html'

# 更新日志可能在仓库根，也可能还在打包源里 —— 两个位置都找
$ChangeLog = $null
$candidates = @()
if (-not [string]::IsNullOrWhiteSpace($ChangeLogPath)) { $candidates += $ChangeLogPath }
$candidates += (Join-Path $root '更新日志.txt')
$candidates += (Join-Path $root '打包源\payload\更新日志.txt')
    $candidates += (Join-Path $root 'docs\更新日志.txt')
foreach ($cand in $candidates) {
    if (Test-Path -LiteralPath $cand -PathType Leaf) { $ChangeLog = $cand; break }
}

# ─────────────────────────────────────────────────────────────
#  前置检查
# ─────────────────────────────────────────────────────────────

Write-Head 'Local Models · 构建'

Write-Host '  仓库根目录  ' -ForegroundColor DarkGray -NoNewline
Write-Host $root -ForegroundColor Gray
Write-Host '  源码目录    ' -ForegroundColor DarkGray -NoNewline
Write-Host $SourceDir -ForegroundColor Gray
Write-Host '  输出目录    ' -ForegroundColor DarkGray -NoNewline
Write-Host $OutDir -ForegroundColor Gray
Write-Host '  编译目标    ' -ForegroundColor DarkGray -NoNewline
Write-Host $Target -ForegroundColor Cyan
Write-Host ''

if (-not (Test-Path -LiteralPath $Csc -PathType Leaf)) {
    Write-Host '找不到 C# 编译器：' -ForegroundColor Red
    Write-Host "  $Csc" -ForegroundColor Red
    Write-Host ''
    Write-Host '请确认已启用 .NET Framework 4.x：' -ForegroundColor Yellow
    Write-Host '  Windows 功能 → .NET Framework 4.x → 启用' -ForegroundColor Yellow
    Write-Host '  Windows 11 默认已启用，无需安装 Visual Studio。' -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path -LiteralPath $SourceDir -PathType Container)) {
    Write-Host "源码目录不存在：$SourceDir" -ForegroundColor Red
    Write-Host '用 -SourceDir 指定源码位置。' -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path -LiteralPath $OutDir -PathType Container)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    Write-Info "已创建输出目录 $OutDir"
}

# 版本号（从 Version.cs 里读，唯一来源）
$versionFile = Join-Path $SourceDir 'Version.cs'
$versionText = '未知'
if (Test-Path -LiteralPath $versionFile -PathType Leaf) {
    $m = Select-String -LiteralPath $versionFile -Pattern 'Version\s*=\s*"([^"]+)"' -List
    if ($m) { $versionText = $m.Matches[0].Groups[1].Value }
}
Write-Host '  版本号      ' -ForegroundColor DarkGray -NoNewline
Write-Host $versionText -ForegroundColor Magenta
Write-Host ''

# ─────────────────────────────────────────────────────────────
#  编译核心
# ─────────────────────────────────────────────────────────────

$CommonRefs = @(
    '/reference:System.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
)

$CommonFlags = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    "/define:$($Configuration.ToUpperInvariant())"
)

if ($Configuration -eq 'Release') { $CommonFlags += '/optimize+' }

<#
    逐个编译指定的 exe。

    $Name      目标显示名
    $OutFile   输出文件名
    $Sources   源文件名数组（相对 $SourceDir）
    $ExtraArgs 额外的 csc 参数（资源嵌入等）
#>
function Invoke-Build {
    param(
        [string]   $Name,
        [string]   $OutFile,
        [string[]] $Sources,
        [string[]] $ExtraArgs = @()
    )

    Write-Step "$Name  →  $OutFile"

    $outPath = Join-Path $OutDir $OutFile

    # 检查源文件是否齐全
    $missing = @()
    foreach ($s in $Sources) {
        $full = Join-Path $SourceDir $s
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { $missing += $s }
    }
    if ($missing.Count -gt 0) {
        foreach ($m in $missing) { Write-Fail "缺少源文件：src\$m" }
        Write-Warn "跳过 $Name（源码不完整）"
        return $false
    }

    $srcPaths = @()
    foreach ($s in $Sources) { $srcPaths += (Join-Path $SourceDir $s) }

    $cscArgs = @()
    $cscArgs += $CommonFlags
    $cscArgs += "/out:$outPath"
    $cscArgs += $ExtraArgs
    $cscArgs += $CommonRefs
    $cscArgs += $srcPaths

    $output = & $Csc $cscArgs 2>&1
    $code   = $LASTEXITCODE

    if ($code -ne 0) {
        Write-Fail "编译失败（csc 退出码 $code）"
        Write-Host ''
        foreach ($line in $output) {
            $text = "$line"
            if ($text -match 'error\s+CS') {
                Write-Host "        $text" -ForegroundColor Red
            } else {
                Write-Host "        $text" -ForegroundColor DarkGray
            }
        }
        Write-Host ''
        Write-Host '    提示：本编译器只支持 C# 5，请检查是否用了下面这些语法：' -ForegroundColor Yellow
        Write-Host '          字符串插值 $"" ... ""、out var、表达式体成员 =>、nameof、?.' -ForegroundColor Yellow
        Write-Host ''
        return $false
    }

    # csc 有时在成功时也会吐警告，照实显示
    $warns = @($output | Where-Object { "$_" -match 'warning\s+CS' })
    if ($warns.Count -gt 0) {
        Write-Warn "$($warns.Count) 条编译警告"
        foreach ($w in $warns) { Write-Host "        $w" -ForegroundColor DarkYellow }
    }

    $size = (Get-Item -LiteralPath $outPath).Length
    $kb   = [math]::Round($size / 1KB, 1)
    Write-Ok ("{0}  ({1:N0} 字节 / {2} KB)" -f $OutFile, $size, $kb)
    return $true
}

# ─────────────────────────────────────────────────────────────
#  各目标的编译参数
# ─────────────────────────────────────────────────────────────

function Build-Launcher {
    $args = @(
        "/win32icon:$Icon"
        "/resource:$Icon,app.ico"
    )
    if (Test-Path -LiteralPath $Html -PathType Leaf) {
        $args += "/resource:$Html,index.html"
    } else {
        Write-Warn "找不到网页源码 $Html —— 启动器将不带内置聊天页面"
    }

    Invoke-Build -Name '启动器' -OutFile 'Local Models.exe' -ExtraArgs $args -Sources @(
        'Version.cs'
        'BonsaiTray.cs'
        'Theme.cs'
        'Win11Menu.cs'
        'Win11Tabs.cs'
        'WebHost.cs'
        'LaunchConfig.cs'
        'Hardware.cs'
        'AccessConfig.cs'
        'ApiKeys.cs'
        'Reasoning.cs'
        'DiskKv.cs'
        'Kvmem.cs'
        'EngineKey.cs'
        'W11Ui.cs'
    )
}

function Build-Installer {
    Invoke-Build -Name '安装程序' -OutFile 'Bonsai2-Setup.exe' -ExtraArgs @(
        "/win32icon:$Icon"
        "/resource:$Icon,app.ico"
    ) -Sources @(
        'Version.cs'
        'Installer.cs'
        'InstallCore.cs'
        'EngineKit.cs'
        'EngineDownload.cs'
    )
}

function Build-Uninstaller {
    Invoke-Build -Name '卸载程序' -OutFile 'Uninstall.exe' -ExtraArgs @(
        "/win32icon:$Icon"
        "/resource:$Icon,app.ico"
    ) -Sources @(
        'Version.cs'
        'Uninstall.cs'
        'Theme.cs'
    )
}

function Build-Updater {
    # 更新器内嵌的是**已经编译好的** exe，所以必须最后编。
    $launcher = Join-Path $OutDir 'Local Models.exe'
    $uninst   = Join-Path $OutDir 'Uninstall.exe'

    $extra = @("/win32icon:$Icon", "/resource:$Icon,app.ico")
    $ok = $true

    if (Test-Path -LiteralPath $launcher -PathType Leaf) {
        $extra += "/resource:$launcher,Local Models.exe"
    } else {
        Write-Fail "内嵌资源缺失：$launcher（请先编 launcher）"
        $ok = $false
    }

    if (Test-Path -LiteralPath $uninst -PathType Leaf) {
        $extra += "/resource:$uninst,Uninstall.exe"
    } else {
        Write-Fail "内嵌资源缺失：$uninst（请先编 uninstaller）"
        $ok = $false
    }

    if (Test-Path -LiteralPath $Html -PathType Leaf) {
        $extra += "/resource:$Html,index.html"
    } else {
        Write-Fail "内嵌资源缺失：$Html"
        $ok = $false
    }

    if ($null -ne $ChangeLog) {
        $extra += "/resource:$ChangeLog,更新日志.txt"
    } else {
        Write-Warn "找不到 更新日志.txt —— 更新器将不带更新日志"
    }

    if (-not $ok) { return $false }

    Invoke-Build -Name '增量更新包' -OutFile 'LocalModels-Update.exe' -ExtraArgs $extra -Sources @(
        'Version.cs'
        'Updater.cs'
        'UpdaterForm.cs'
        'UpdateMain.cs'
    )
}

# ─────────────────────────────────────────────────────────────
#  执行
# ─────────────────────────────────────────────────────────────

Write-Head "编译（$Configuration）"

$results = [ordered]@{}

switch ($Target) {
    'launcher'    { $results['launcher']    = Build-Launcher }
    'installer'   { $results['installer']   = Build-Installer }
    'uninstaller' { $results['uninstaller'] = Build-Uninstaller }
    'updater'     { $results['updater']     = Build-Updater }

    'all' {
        # 顺序不能变：更新器依赖前面三个的产物
        $results['launcher']    = Build-Launcher
        $results['installer']   = Build-Installer
        $results['uninstaller'] = Build-Uninstaller
        $results['updater']     = Build-Updater
    }
}

# ─────────────────────────────────────────────────────────────
#  汇总
# ─────────────────────────────────────────────────────────────

Write-Head '结果'

$okCount = 0
foreach ($k in $results.Keys) {
    $v = $results[$k]
    if ($v) {
        $okCount++
        Write-Host '  ✓ ' -ForegroundColor Green -NoNewline
        Write-Host $k.PadRight(14) -ForegroundColor Gray -NoNewline
        Write-Host '成功' -ForegroundColor Green
    } else {
        Write-Host '  ✗ ' -ForegroundColor Red -NoNewline
        Write-Host $k.PadRight(14) -ForegroundColor Gray -NoNewline
        Write-Host '失败' -ForegroundColor Red
    }
}

Write-Host ''
Write-Host "  产物目录：$OutDir" -ForegroundColor DarkGray
Write-Host ''

if ($okCount -eq $results.Count) {
    Write-Host '  全部编译成功。' -ForegroundColor Green
    Write-Host ''
    Write-Host '  下一步：把产物同步进打包源的 payload 目录，再重新出包。' -ForegroundColor DarkGray
    Write-Host '  注意安装程序释放的是 payload 里的那一份，不是这里的。' -ForegroundColor DarkGray
    Write-Host ''
    exit 0
} else {
    Write-Host "  $($results.Count - $okCount) 个目标失败，请检查上面的红色输出。" -ForegroundColor Red
    Write-Host ''
    exit 1
}
