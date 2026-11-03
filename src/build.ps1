# AI 사용량 위젯 빌드
# 저장소 폴더에서 실행:  powershell -ExecutionPolicy Bypass -File src\build.ps1
# 결과: 저장소 폴더에 AiUsageWidget.exe (+ Microsoft WebView2 DLL 3개)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$src  = $PSScriptRoot
$root = Split-Path -Parent $src
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw '.NET Framework 4.x C# compiler (csc.exe) not found.' }

# Microsoft WebView2 SDK (NuGet): DLL이 없을 때만 내려받는다
$wvVersion = '1.0.4314.35'
$dlls = @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll')
$missing = $dlls | Where-Object { -not (Test-Path (Join-Path $root $_)) }
if ($missing) {
    Write-Host "Downloading Microsoft.Web.WebView2 $wvVersion from nuget.org ..."
    $work = Join-Path $env:TEMP ('webview2-sdk-' + $wvVersion)
    $zip  = $work + '.zip'
    Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/$wvVersion" -OutFile $zip -UseBasicParsing
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    Expand-Archive -LiteralPath $zip -DestinationPath $work -Force
    Copy-Item (Join-Path $work 'lib\net462\Microsoft.Web.WebView2.Core.dll') $root -Force
    Copy-Item (Join-Path $work 'lib\net462\Microsoft.Web.WebView2.WinForms.dll') $root -Force
    Copy-Item (Join-Path $work 'runtimes\win-x64\native\WebView2Loader.dll') $root -Force
    Remove-Item $work -Recurse -Force
    Remove-Item $zip -Force
}

# 아이콘: 저장소 폴더에 ai-usage.ico가 있으면 그것을, 없으면 기본 아이콘(src\app.ico)
$icon = Join-Path $root 'ai-usage.ico'
if (-not (Test-Path $icon)) { $icon = Join-Path $src 'app.ico' }

& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    "/out:$root\AiUsageWidget.exe" "/win32icon:$icon" "/win32manifest:$src\app.manifest" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    "/r:$root\Microsoft.Web.WebView2.Core.dll" "/r:$root\Microsoft.Web.WebView2.WinForms.dll" `
    "$src\AiUsageWidget.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
Write-Host "Done: $root\AiUsageWidget.exe"
