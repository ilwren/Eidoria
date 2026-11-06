<#
.SYNOPSIS
  一条命令做端到端验收（真实权重）：确保模型就绪 -> 跑整条语义搜索链路 -> 打印报告。

.EXAMPLE
  pwsh scripts/accept-real-models.ps1

.EXAMPLE
  pwsh scripts/accept-real-models.ps1 -Models D:\models -Out D:\report.md -SkipDownload
#>
[CmdletBinding()]
param(
    [string]$Models = $(if ($env:MDEXPLORER_MODELS) { $env:MDEXPLORER_MODELS } else { Join-Path $PSScriptRoot '..\models' }),
    [string]$Out = (Join-Path $PSScriptRoot '..\artifacts\real-model-acceptance\report.md'),
    [switch]$SkipDownload
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fetchScript = Join-Path $PSScriptRoot 'fetch-ai-models.ps1'
$modelscopeScript = Join-Path $PSScriptRoot 'fetch-ai-models-modelscope.py'
$testsProject = Join-Path $root 'tests/MdExplorer.Tests'

function Write-Step([string]$message) { Write-Host "== $message" }

function Test-ModelsReady([string]$Directory) {
    if (-not $Directory) { $Directory = $Models }
    try {
        & $fetchScript -Dir $Directory -Verify *> $null
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error '需要 .NET SDK 10（dotnet 不在 PATH 上）'
    exit 1
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null

if (Test-ModelsReady -Directory $Models) {
    Write-Step "模型已就绪且校验通过：$Models"
}
elseif ($SkipDownload) {
    Write-Error "模型不完整：$Models（-SkipDownload 要求它已就绪且校验通过）"
    exit 2
}
else {
    Write-Step '模型不完整，开始按 huggingface.co -> hf-mirror.com -> modelscope.cn 顺序补齐'
    & $fetchScript -Dir $Models
    if (Test-ModelsReady -Directory $Models) {
        Write-Step '模型就绪（huggingface.co）'
    }
    else {
        & $fetchScript -Dir $Models -Mirror https://hf-mirror.com
        if (Test-ModelsReady -Directory $Models) {
            Write-Step '模型就绪（hf-mirror.com）'
        }
        else {
            $staging = "$Models.modelscope"
            if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
            $python = Get-Command python -ErrorAction SilentlyContinue
            if ($python) {
                & $python.Source $modelscopeScript --dir $staging
                if ((Test-Path $staging) -and (Test-ModelsReady -Directory $staging)) {
                    Remove-Item -Recurse -Force $Models -ErrorAction SilentlyContinue
                    Move-Item $staging $Models
                    Write-Step '模型就绪（modelscope.cn）'
                }
                else {
                    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
                    Write-Error '三个来源都没能补齐模型；请检查网络或手动运行 scripts/fetch-ai-models.ps1'
                    exit 2
                }
            }
            else {
                Write-Error '需要 Python 3 才能用魔搭源补齐模型；也可以手动运行 scripts/fetch-ai-models.ps1'
                exit 2
            }
        }
    }
}

$env:MDEXPLORER_RUN_REAL_MODEL_TESTS = '1'
$env:MDEXPLORER_MODELS = (Resolve-Path $Models).Path
$env:MDEXPLORER_ACCEPTANCE_REPORT = $Out

Write-Step '运行真实权重验收（首次会加载 ONNX Session，CPU 上约数十秒）'
$log = Join-Path ([System.IO.Path]::GetTempPath()) ("eidoria-acceptance-" + [guid]::NewGuid().ToString('N') + '.log')
& dotnet test $testsProject -c Release --filter 'FullyQualifiedName~RealModelAcceptanceTests' 2>&1 |
    Tee-Object -FilePath $log |
    ForEach-Object { "   $_" }

$passed = Select-String -Path $log -Pattern 'Passed!' -Quiet
Remove-Item $log -ErrorAction SilentlyContinue
if (-not $passed) {
    Write-Error '验收没通过（完整输出见上方）；报告可能未生成'
    exit 3
}

if (Test-Path $Out) {
    Write-Host ''
    Write-Host '================ 验收报告 ================'
    Get-Content $Out | Write-Host
    Write-Host '========================================='
    Write-Host "报告已保存：$Out"
}
else {
    Write-Host "验收通过，但没找到报告文件（预期：$Out）"
}
