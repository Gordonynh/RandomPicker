#Requires -Version 5.1
<#
.SYNOPSIS
    编译 ClassIsland.RandomPicker 插件，并打包成 ClassIsland 的 .cipx 插件包。

.DESCRIPTION
    环境要求：
      * .NET 8 SDK        https://dotnet.microsoft.com/download/dotnet/8.0
      * Windows PowerShell 5.1 或 PowerShell 7+
      * 本仓库位于 ClassIsland 源码树的 plugins/RandomPicker 下
        （工程用 ProjectReference 引用 ClassIsland.Core，位置不对会编译失败）

.PARAMETER Configuration
    编译配置，默认 Release。

.PARAMETER SkipBuild
    跳过编译，直接用现有输出目录打包（改打包清单时用得上）。

.EXAMPLE
    ./build.ps1

.EXAMPLE
    ./build.ps1 -Configuration Debug

.EXAMPLE
    ./build.ps1 -SkipBuild
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$ProjectName  = "ClassIsland.RandomPicker"
$AssemblyName = "$ProjectName.dll"
$Tfm          = "net8.0"
$ProjectDir   = Join-Path $PSScriptRoot "src"
$ProjectPath  = Join-Path $ProjectDir "$ProjectName.csproj"

# ---------------------------------------------------------------- 0. 环境检查

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "找不到 dotnet 命令。请先安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0"
}

# 机器上可能只装了更高版本的 SDK：那样 global.json 会把它挡掉，编不过。
$sdks = & dotnet --list-sdks 2>$null
if ($LASTEXITCODE -ne 0 -or -not $sdks) {
    throw "无法读取 SDK 列表（dotnet --list-sdks 失败）。请确认装的是 .NET SDK 而不是仅运行时。"
}

if (-not ($sdks | Where-Object { $_ -match '^8\.' })) {
    throw ("需要 .NET 8 SDK，当前只找到：" + [Environment]::NewLine +
           ($sdks -join [Environment]::NewLine) + [Environment]::NewLine +
           "下载：https://dotnet.microsoft.com/download/dotnet/8.0")
}

if (-not (Test-Path $ProjectPath)) {
    throw "找不到工程文件：$ProjectPath"
}

# 工程引用 ../.. 的 ClassIsland.Core，所以仓库必须放在宿主的 plugins 目录下。
$CoreProject = Join-Path $PSScriptRoot "..\..\ClassIsland.Core\ClassIsland.Core.csproj"
if (-not (Test-Path $CoreProject)) {
    Write-Warning "没在 ../../ClassIsland.Core 找到宿主源码。构建大概率会失败——请把仓库放到 ClassIsland 源码树的 plugins/RandomPicker 下。"
}

# ---------------------------------------------------------------- 1. 编译

if ($SkipBuild) {
    Write-Host "==> 跳过编译（-SkipBuild）" -ForegroundColor Cyan
}
else {
    Write-Host "==> 编译 $ProjectName ($Configuration)" -ForegroundColor Cyan
    & dotnet build $ProjectPath -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE），已中止打包。" }
}

$OutDir = Join-Path $ProjectDir "bin/$Configuration/$Tfm"
if (-not (Test-Path $OutDir)) { throw "找不到输出目录：$OutDir" }

# ---------------------------------------------------------------- 2. 同步元数据

# 清单 / 图标 / 说明在仓库根目录，csproj 用 Link 复制到输出目录。但 MSBuild 的复制会带
# 时间戳，判断「输出比源新」时可能误判，于是跳过复制 —— 打出来的包里还是上一版清单。
# 实测踩过：manifest.yml 改成 2.0.0.0，包里躺着的却是 1.1.0.0。
# 这里无条件再同步一次，-SkipBuild 时也照样生效。
foreach ($meta in @("manifest.yml", "README.md", "icon.png")) {
    $metaSrc = Join-Path $PSScriptRoot $meta
    if (Test-Path $metaSrc) {
        Copy-Item -LiteralPath $metaSrc -Destination (Join-Path $OutDir $meta) -Force
    }
}

# ---------------------------------------------------------------- 3. 收集打包内容

# .cipx 就是一个普通 zip，宿主安装时整个解压到 Plugins/<id>/ 下，
# 并且用 pkg.GetEntry("manifest.yml") 取清单 —— 所以所有文件必须在 zip 根目录，不能有外层文件夹。
#
# 打包策略：只放插件自己的东西。宿主已经加载的那些程序集（ClassIsland.Core / Shared 等）
# 一律不带，插件包里再塞一份会重复初始化。这和 csproj 里 Private=false 的用意一致。
# 除插件 dll / 清单 / 图标 / 说明之外还需要什么，写在 pack-include.txt 里（ONNX Runtime 那几样）。

$Include = New-Object System.Collections.Generic.List[string]
$Include.Add($AssemblyName)
$Include.Add("manifest.yml")

# 清单里引用的两个文件，缺了只是难看，不至于装不上，所以允许缺席。
foreach ($opt in @("icon.png", "README.md")) {
    if (Test-Path (Join-Path $OutDir $opt)) { $Include.Add($opt) }
    else { Write-Warning "输出目录里没有 $opt，跳过（manifest.yml 里引用了它）。" }
}

$PackListPath = Join-Path $PSScriptRoot "pack-include.txt"
if (Test-Path $PackListPath) {
    foreach ($line in (Get-Content $PackListPath -Encoding UTF8)) {
        $t = $line.Trim()
        if ($t -eq "" -or $t.StartsWith("#")) { continue }
        if (-not $Include.Contains($t)) { $Include.Add($t) }
    }
}

# 校验必备文件都在
$Paths   = New-Object System.Collections.Generic.List[string]
$Missing = New-Object System.Collections.Generic.List[string]
foreach ($name in $Include) {
    $p = Join-Path $OutDir $name
    if (Test-Path $p) { $Paths.Add($p) } else { $Missing.Add($name) }
}
if ($Missing.Count -gt 0) { throw "输出目录缺少以下文件，无法打包：$($Missing -join ', ')" }

# ---------------------------------------------------------------- 4. 打 zip

$CipxDir  = Join-Path $PSScriptRoot "cipx"
$CipxPath = Join-Path $CipxDir "$ProjectName.cipx"

if (Test-Path $CipxDir)  { Remove-Item $CipxDir  -Recurse -Force }
New-Item -ItemType Directory -Path $CipxDir | Out-Null

Write-Host "==> 打包 $CipxPath" -ForegroundColor Cyan
# Compress-Archive 只接受 .zip 后缀，所以先压成 zip 再改名。
$ZipPath = Join-Path $CipxDir "$ProjectName.zip"
Compress-Archive -LiteralPath $Paths -DestinationPath $ZipPath -CompressionLevel Optimal
Move-Item -Path $ZipPath -Destination $CipxPath -Force

# ---------------------------------------------------------------- 5. 自检

# 再打开一次，确认清单真的在 zip 根目录 —— 装不上的绝大多数原因就是它被套了一层文件夹。
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($CipxPath)
try {
    $entries = $zip.Entries | ForEach-Object { $_.FullName }
    if ($entries -notcontains "manifest.yml") {
        throw "打包结果里没有根目录的 manifest.yml，宿主将无法识别这个插件。"
    }

    # 包里的清单必须就是仓库里那份。版本号打错是最难发现的问题之一：
    # 装上去看着一切正常，只有插件列表里的版本号不对。这里直接比字节。
    $manifestEntry = $zip.GetEntry("manifest.yml")
    $reader = New-Object System.IO.StreamReader($manifestEntry.Open(), [System.Text.Encoding]::UTF8)
    try { $packedManifest = $reader.ReadToEnd() } finally { $reader.Dispose() }

    $sourceManifest = Get-Content (Join-Path $PSScriptRoot "manifest.yml") -Raw -Encoding UTF8
    if ($packedManifest.Replace("`r`n", "`n").TrimEnd() -ne $sourceManifest.Replace("`r`n", "`n").TrimEnd()) {
        throw "包里的 manifest.yml 和仓库根目录那份不一致（多半是输出了旧副本）。删掉 src/bin 和 src/obj 再打一次。"
    }

    Write-Host "    包含 $(@($entries).Count) 个条目：" -ForegroundColor DarkGray
    foreach ($e in $entries) { Write-Host "      - $e" -ForegroundColor DarkGray }
}
finally { $zip.Dispose() }

# ---------------------------------------------------------------- 6. 校验和

# 和官方 PluginSdk 的 generate-md5.ps1 保持一致：往 checksums.md 里写一段带 JSON 的 HTML 注释，
# 插件市场用它校验下载到的包。
$hash = (Get-FileHash $CipxPath -Algorithm MD5).Hash
$json = ConvertTo-Json @{(Split-Path $CipxPath -Leaf) = $hash} -Compress
Set-Content -Path (Join-Path $CipxDir "checksums.md") `
            -Value "`n<!-- CLASSISLAND_PKG_MD5 $json -->" -Encoding UTF8

# ---------------------------------------------------------------- 7. 收尾

$sizeMb = [math]::Round((Get-Item $CipxPath).Length / 1MB, 2)
Write-Host ""
Write-Host "完成：$CipxPath ($sizeMb MB)  MD5=$hash" -ForegroundColor Green
Write-Host "拖进 ClassIsland 窗口，或放进 应用数据/Cache/PluginPackages/ 即可安装。" -ForegroundColor DarkGray
