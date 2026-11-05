<#
.SYNOPSIS
  Eidoria（万象屿）端侧 AI 模型下载脚本（Windows / PowerShell 7）。

.DESCRIPTION
  按 scripts/ai-models.manifest 下载模型权重与分词器到应用数据目录，
  首次下载后把 SHA-256 记进 checksums.txt，之后每次运行都按它校验。

  模型体积几十 MB，不能塞进 git；应用又必须在没有网络的情况下才算「端侧」，
  所以把「取模型」做成一个显式动作。

.EXAMPLE
  pwsh scripts/fetch-ai-models.ps1
.EXAMPLE
  pwsh scripts/fetch-ai-models.ps1 -List
.EXAMPLE
  pwsh scripts/fetch-ai-models.ps1 -Only text-embed-zh -Dir D:\models
.EXAMPLE
  pwsh scripts/fetch-ai-models.ps1 -Only audio-asr-tiny
.EXAMPLE
  pwsh scripts/fetch-ai-models.ps1 -Only audio-classify-yamnet
.EXAMPLE
  pwsh scripts/fetch-ai-models.ps1 -Only image-vlm-smol
.EXAMPLE
  pwsh scripts/fetch-ai-models.ps1 -All
.EXAMPLE
  $env:HF_ENDPOINT = 'https://hf-mirror.com'; pwsh scripts/fetch-ai-models.ps1
#>
[CmdletBinding()]
param(
    [switch]$List,
    [switch]$Verify,
    [switch]$Clean,
    [switch]$Force,
    [switch]$DryRun,
    [switch]$All,
    [string]$Only,
    [string]$Dir,
    [string]$Mirror
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ManifestPath = if ($env:AI_MODELS_MANIFEST) { $env:AI_MODELS_MANIFEST } else { Join-Path $ScriptDir 'ai-models.manifest' }

$mirrorBase = if ($Mirror) { $Mirror } elseif ($env:HF_ENDPOINT) { $env:HF_ENDPOINT } else { 'https://huggingface.co' }
$mirrorBase = $mirrorBase.TrimEnd('/')

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    Write-Error "找不到模型清单：$ManifestPath"
    exit 1
}

# ── 解析清单（纯文本；bash 与 PowerShell 读同一份） ─────────────────────────
$models = [ordered]@{}
foreach ($raw in Get-Content -LiteralPath $ManifestPath -Encoding UTF8) {
    $line = $raw.Trim()
    if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
    $parts = $line.Split('|')
    switch ($parts[0]) {
        'model' {
            if ($parts.Count -ne 11) { throw "清单行格式不对（model 需要 11 列）：$line" }
            if ($parts[7] -notmatch '^[0-9a-f]{40}$') { throw "模型 revision 必须是 40 位小写十六进制 SHA：$($parts[1])" }
            $models[$parts[1]] = [pscustomobject]@{
                Id = $parts[1]; Name = $parts[2]; Purpose = $parts[3]
                License = $parts[4]; LicenseUrl = $parts[5]
                Source = $parts[6]; Revision = $parts[7]; Runtime = $parts[8]; SizeMb = $parts[9]
                Default = [bool]::Parse($parts[10])
                Files = [System.Collections.Generic.List[string]]::new()
            }
        }
        'file' {
            if ($parts.Count -ne 3) { throw "清单行格式不对（file 需要 3 列）：$line" }
            if (-not $models.Contains($parts[1])) { throw "清单里 file 指向了未知模型：$($parts[1])" }
            $models[$parts[1]].Files.Add($parts[2])
        }
        default { throw "清单里有不认识的类型：$($parts[0])" }
    }
}
if ($models.Count -eq 0) { throw '清单里没有任何模型' }

function Test-Wanted([string]$id) {
    if ($Only) { return ($Only.Split(',') -contains $id) }
    if ($All) { return $true }
    return $models[$id].Default
}

# ── 目录 ────────────────────────────────────────────────────────────────────
$TargetDir =
    if ($Dir) { $Dir }
    elseif ($env:MDEXPLORER_MODELS) { $env:MDEXPLORER_MODELS }
    elseif ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'MdExplorer\models' }
    else { Join-Path $HOME '.local/share/MdExplorer/models' }
$TargetDir = $TargetDir.TrimEnd([char]92, [char]47)
$ChecksumsPath = Join-Path $TargetDir 'checksums.txt'

function Format-Size([long]$bytes) {
    if ($bytes -ge 1GB) { return ('{0:N1} GiB' -f ($bytes / 1GB)) }
    if ($bytes -ge 1MB) { return ('{0:N1} MiB' -f ($bytes / 1MB)) }
    if ($bytes -ge 1KB) { return ('{0:N1} KiB' -f ($bytes / 1KB)) }
    return "$bytes B"
}

function Get-ChecksumMap {
    $map = @{}
    if (Test-Path -LiteralPath $ChecksumsPath) {
        foreach ($line in Get-Content -LiteralPath $ChecksumsPath) {
            if ($line -match '^([0-9a-fA-F]{64})\s{2}(.+)$') { $map[$Matches[2]] = $Matches[1].ToLowerInvariant() }
        }
    }
    return $map
}

function Save-Checksum([string]$key, [string]$hash) {
    $map = Get-ChecksumMap
    $map[$key] = $hash
    New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
    $lines = $map.GetEnumerator() | Sort-Object Key | ForEach-Object { "$($_.Value)  $($_.Key)" }
    Set-Content -LiteralPath $ChecksumsPath -Value $lines -Encoding ascii
}

function Get-Sha256([string]$path) {
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ── --list ──────────────────────────────────────────────────────────────────
if ($List) {
    Write-Host "模型清单：$ManifestPath"
    Write-Host "下载目录：$TargetDir"
    Write-Host ''
    foreach ($id in $models.Keys) {
        $m = $models[$id]
        Write-Host ("  {0,-16} {1}" -f $id, $m.Name)
        Write-Host ("  {0,-16} 用途：{1}" -f '', $m.Purpose)
        Write-Host ("  {0,-16} 许可：{1}（{2}）" -f '', $m.License, $m.LicenseUrl)
        $pack = if ($m.Default) { '默认轻量包' } else { '可选，需 -Only/-All' }
        Write-Host ("  {0,-16} 仓库：{1} · 运行时 {2} · 约 {3} MB · {4} 个文件 · {5}" -f '', ("{0}@{1}" -f $m.Source, $m.Revision.Substring(0, 8)), $m.Runtime, $m.SizeMb, $m.Files.Count, $pack)
        Write-Host ''
    }
    Write-Host "镜像：$mirrorBase（用 -Mirror 或环境变量 HF_ENDPOINT 覆盖）"
    Write-Host '无参数时只取默认轻量包；可选音频/图片模型要 -Only <id> 或 -All。'
    exit 0
}

# ── --clean ─────────────────────────────────────────────────────────────────
if ($Clean) {
    $checksums = Get-ChecksumMap
    $removed = 0
    foreach ($id in $models.Keys) {
        if (-not (Test-Wanted $id)) { continue }
        foreach ($rel in $models[$id].Files) {
            $path = Join-Path $TargetDir (Join-Path $id $rel)
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force; $removed++ }
            if (Test-Path -LiteralPath "$path.part") { Remove-Item -LiteralPath "$path.part" -Force }
        }
    }
    # 只移除所选模型的校验记录；清理中文模型不应影响仍安装的英文模型。
    if (Test-Path -LiteralPath $ChecksumsPath) {
        $selectedPrefixes = @($models.Keys | Where-Object { Test-Wanted $_ } | ForEach-Object { "$_/" })
        $remaining = @($checksums.GetEnumerator() | Where-Object {
            $key = $_.Key
            -not ($selectedPrefixes | Where-Object { $key.StartsWith($_, [StringComparison]::Ordinal) })
        } | Sort-Object Key | ForEach-Object { "$($_.Value)  $($_.Key)" })
        if ($remaining.Count -gt 0) { Set-Content -LiteralPath $ChecksumsPath -Value $remaining -Encoding ascii }
        else { Remove-Item -LiteralPath $ChecksumsPath -Force }
    }
    Write-Host "已删除 $removed 个模型文件（目录：$TargetDir）"
    exit 0
}

$checksums = Get-ChecksumMap

# ── --verify ────────────────────────────────────────────────────────────────
if ($Verify) {
    if (-not (Test-Path -LiteralPath $ChecksumsPath)) {
        Write-Error "$TargetDir 里没有 checksums.txt —— 先正常跑一次下载再校验"
        exit 1
    }
    $bad = 0; $missing = 0; $untracked = 0
    foreach ($id in $models.Keys) {
        if (-not (Test-Wanted $id)) { continue }
        foreach ($rel in $models[$id].Files) {
            $key = "$id/$rel"
            $path = Join-Path $TargetDir (Join-Path $id $rel)
            if (-not (Test-Path -LiteralPath $path)) { Write-Warning "缺少文件：$key"; $missing++; continue }
            if (-not $checksums.ContainsKey($key)) { Write-Warning "没有记录校验和：$key"; $untracked++; continue }
            if ((Get-Sha256 $path) -ne $checksums[$key]) { Write-Warning "校验失败：$key"; $bad++ }
        }
    }
    if ($bad -gt 0 -or $missing -gt 0 -or $untracked -gt 0) {
        Write-Host "校验未通过：$bad 个损坏、$missing 个缺失、$untracked 个无校验记录（可正常重跑补齐，或加 -Force 重新下载）"
        exit 2
    }
    Write-Host '校验通过：清单内文件全部与 checksums.txt 一致'
    exit 0
}

# ── 下载 ────────────────────────────────────────────────────────────────────
function Invoke-Download([string]$url, [string]$partial) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $partial) | Out-Null
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        # 只对 .part 断点续传；完整且校验通过后才原子改名。
        & $curl.Source -sS -fL --retry 3 --retry-delay 2 --connect-timeout 20 -C - -o $partial $url
        return ($LASTEXITCODE -eq 0)
    }
    try {
        Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing
        return $true
    } catch {
        Write-Warning $_.Exception.Message
        return $false
    }
}

$ok = 0; $skip = 0; $fail = 0
foreach ($id in $models.Keys) {
    if (-not (Test-Wanted $id)) { continue }
    $m = $models[$id]
    Write-Host "== $($m.Name)（$($m.License)，约 $($m.SizeMb) MB）"
    Write-Host "   仓库 $($m.Source) · 运行时 $($m.Runtime)"

    foreach ($rel in $m.Files) {
        $key = "$id/$rel"
        $url = "$mirrorBase/$($m.Source)/resolve/$($m.Revision)/$rel"
        $dest = Join-Path $TargetDir (Join-Path $id $rel)
        $partial = "$dest.part"

        if ((Test-Path -LiteralPath $dest) -and -not $Force) {
            if ($checksums.ContainsKey($key)) {
                if ((Get-Sha256 $dest) -eq $checksums[$key]) {
                    Write-Host ("   已就绪 {0}  {1}" -f (Format-Size (Get-Item -LiteralPath $dest).Length), $key)
                    $skip++
                    continue
                }
                Write-Warning "文件与记录的校验和不一致，重新下载：$key"
                if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
            } else {
                Write-Host "   已存在（本次记录校验和）  $key"
                Save-Checksum $key (Get-Sha256 $dest)
                $skip++
                continue
            }
        }

        if ($DryRun) {
            Write-Host "   [dry-run] $url"
            Write-Host "             -> $dest"
            continue
        }

        if ($Force -and (Test-Path -LiteralPath $partial)) { Remove-Item -LiteralPath $partial -Force }
        if (Invoke-Download $url $partial) {
            $size = (Get-Item -LiteralPath $partial).Length
            if ($size -lt 1024) {
                Write-Warning "下载到的文件只有 $size 字节，看起来不是模型：$key"
                Remove-Item -LiteralPath $partial -Force
                $fail++
                continue
            }
            $hash = Get-Sha256 $partial
            if ($checksums.ContainsKey($key) -and $hash -ne $checksums[$key]) {
                Write-Warning "校验失败：$key"
                Remove-Item -LiteralPath $partial -Force
                $fail++
                continue
            }
            Move-Item -LiteralPath $partial -Destination $dest -Force
            Save-Checksum $key $hash
            $checksums[$key] = $hash
            Write-Host ("   下载完成 {0}  {1}  sha256 {2}…" -f (Format-Size $size), $key, $hash.Substring(0, 12))
            $ok++
        } else {
            Write-Warning "下载失败：$url"
            Write-Warning "  可换镜像重试：`$env:HF_ENDPOINT='https://hf-mirror.com'; pwsh scripts/fetch-ai-models.ps1"
            $fail++
        }
    }
}

Write-Host ''
Write-Host "完成：新下载 $ok 个、已就绪 $skip 个、失败 $fail 个"
Write-Host "模型目录：$TargetDir"
Write-Host '应用会在这里找模型（也可用环境变量 MDEXPLORER_MODELS 指向别处）：'
Write-Host '  Windows: %LOCALAPPDATA%\MdExplorer\models'
Write-Host "校验随时可重跑：pwsh scripts/fetch-ai-models.ps1 -Verify（可用 -Only 或 -All 选择范围）"

if ($fail -ne 0) { exit 2 }
