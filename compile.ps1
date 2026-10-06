# 定时关机助手 - 单文件编译脚本
# 使用系统自带的 .NET Framework C# 编译器，将单个 ShutdownHelper.cs 编译为 exe
# 无需安装任何额外软件，双击或运行本脚本即可

$ErrorActionPreference = "Stop"

# 编译器路径（.NET Framework 4.x 自带）
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
    Write-Error "未找到 csc.exe 编译器，无法编译。"
    exit 1
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = Split-Path -Parent $csc
$source = Join-Path $scriptDir "ShutdownHelper.cs"
$out = Join-Path $scriptDir "ShutdownHelper.exe"

if (-not (Test-Path $source)) {
    Write-Error "未找到源文件 ShutdownHelper.cs"
    exit 1
}

Write-Host "正在编译单文件 ShutdownHelper.cs ..."
& $csc /nologo /target:winexe /out:$out `
    /win32icon:"$scriptDir\app.ico" `
    /reference:"$fw\System.Windows.Forms.dll" `
    /reference:"$fw\System.Drawing.dll" `
    /reference:"$fw\System.Web.Extensions.dll" `
    /reference:"$fw\Microsoft.CSharp.dll" `
    $source

if ($LASTEXITCODE -eq 0) {
    Write-Host "编译成功：$out" -ForegroundColor Green
} else {
    Write-Host "编译失败，请检查上面的错误信息。" -ForegroundColor Red
    exit 1
}
