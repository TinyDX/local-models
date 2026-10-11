# 打包内嵌资源：把程序本体编进安装器 exe
#
# 为什么需要这一步：
#   用户从 GitHub 只下载一个 Bonsai2-Setup.exe。
#   程序本体（启动器、网页、许可，约 0.4 MB）必须编进 exe 里，
#   否则用户拿不到 —— 他只下了一个文件，exe 旁边不会有 payload\ 目录。
#
# 引擎（4.2 GB）不编进去，按显卡单独下载。
#
# 资源命名规则：
#   路径分隔符 "\" 换成 "__"（资源名不能带反斜杠）
#   文件名保持原样（空格、中文都支持）
#     webui\index.html       → p.webui__index.html
#     licenses\MIT-fmt.txt   → p.licenses__MIT-fmt.txt
#     Local Models.exe       → p.Local Models.exe
#
# 用 "p." 前缀是为了跟其它资源（app.ico、index.html）区分开。

param(
    [string]$PayloadDir = "",
    [string]$OutFile    = "",   # 输出的 csc 参数文件
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path   # ...\打包源
$root = Split-Path -Parent $here                          # 项目根

if ([string]::IsNullOrWhiteSpace($PayloadDir)) { $PayloadDir = Join-Path $here 'payload' }
if ([string]::IsNullOrWhiteSpace($OutFile))    { $OutFile    = Join-Path $root '编译产物\内嵌资源.rsp' }

if (-not (Test-Path $PayloadDir)) { throw "找不到 payload 目录：$PayloadDir" }

$lines  = New-Object System.Collections.Generic.List[string]
$count  = 0
$bytes  = 0

Get-ChildItem $PayloadDir -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($PayloadDir.Length).TrimStart('\')

    # 引擎不内嵌 —— 4.2 GB，按显卡单独下载
    if ($rel -like 'engine\*') { return }

    $resName = 'p.' + ($rel -replace '\\', '__')
    # ★ 路径和资源名都要加引号 —— 文件名里有空格（"Local Models.exe"），
    #   不加引号 csc 会把空格当参数分隔，报 "找不到源文件: Models.exe,p.Local"
    $lines.Add('/resource:"' + $_.FullName + '","' + $resName + '"')
    $count++
    $bytes += $_.Length
}

[IO.File]::WriteAllLines($OutFile, $lines, [Text.UTF8Encoding]::new($false))

if (-not $Quiet) {
    Write-Host ("  内嵌资源: {0} 个文件, {1:N2} MB" -f $count, ($bytes / 1MB))
    Write-Host ("  参数文件: {0}" -f $OutFile)
}
