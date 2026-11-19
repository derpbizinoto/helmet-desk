<#
.SYNOPSIS
  Gera uma versão do Helmet Desk com instalador (Setup.exe + MSI) e atualização automática (Velopack).

.EXAMPLE
  .\build-release.ps1 1.0.0            # só gera em .\releases
  .\build-release.ps1 1.0.1 -Upload    # gera e publica a release v1.0.1 no GitHub

.NOTES
  Requer: .NET SDK 10, vpk (dotnet tool install -g vpk) e, para -Upload, GitHub CLI logado (gh auth login).
  O token do GitHub só existe em memória durante o upload. Nunca é gravado em arquivo.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$Upload
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------- configuração ----------
$RepoUrl    = 'https://github.com/derpbizinoto/helmet-desk'   # mesmo valor de UpdateService.RepoUrl
$PackId     = 'HelmetDesk'
$PackTitle  = 'Helmet Desk'
$MainExe    = 'HelmetDesk.exe'
$Root       = $PSScriptRoot
$Project    = Join-Path $Root 'src\HelmetDesk.csproj'
$Icon       = Join-Path $Root 'src\Assets\icon.ico'
$PublishDir = Join-Path $Root 'publish'
$ReleaseDir = Join-Path $Root 'releases'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Magenta }
function Fail($text) { Write-Host "ERRO: $text" -ForegroundColor Red; exit 1 }

# ferramentas instaladas nesta sessão podem não estar no PATH ainda
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
            [Environment]::GetEnvironmentVariable('Path', 'User') + ';' +
            (Join-Path $env:USERPROFILE '.dotnet\tools')

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet não encontrado. Instale o .NET SDK 10.' }
if (-not (Get-Command vpk -ErrorAction SilentlyContinue))    { Fail 'vpk não encontrado. Instale com: dotnet tool install -g vpk' }
if ($Upload -and -not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail 'gh não encontrado. Instale o GitHub CLI e rode: gh auth login' }

# ---------- 1. publish (Release, win-x64, self-contained) ----------
Step "Publicando Helmet Desk $Version (Release, win-x64, self-contained)"
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
dotnet publish $Project -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false `
    -o $PublishDir --nologo
if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish falhou.' }
if (-not (Test-Path (Join-Path $PublishDir $MainExe))) { Fail "$MainExe não apareceu em $PublishDir." }

# ---------- 1b. nada de dados pessoais no pacote ----------
# o app guarda dados só em %AppData% / %LocalAppData%; isto confere que nenhum arquivo de dados foi parar no publish
$suspect = Get-ChildItem $PublishDir -Recurse -File | Where-Object {
    $_.Name -match '^(data(\..*)?\.json|last-crash\.txt)$' -or $_.Extension -in '.zip', '.log' -or
    $_.FullName -match '\\(attachments|links|backups|relatorios|logs)\\'
}
if ($suspect) { Fail ("Arquivos de dados encontrados no publish (não devem ir no instalador):`n" + ($suspect.FullName -join "`n")) }

# ---------- 1c. nada das ferramentas de desenvolvedor no pacote ----------
# --selftest, --snapshot e --readme-shots só são compilados com DevTools (padrão em Debug; ver src\HelmetDesk.csproj).
# Os textos ficam no .dll em UTF-16, alinhados em byte par ou ímpar: procura nas duas leituras.
$bytes = [IO.File]::ReadAllBytes((Join-Path $PublishDir 'HelmetDesk.dll'))
$utf16 = [Text.Encoding]::Unicode.GetString($bytes) + [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
$devArgs = '--selftest', '--snapshot', '--readme-shots' | Where-Object { $utf16.Contains($_) }
if ($devArgs) { Fail ("O publish contém as ferramentas de desenvolvedor ($($devArgs -join ', ')). Publique sem -p:DevTools=true.") }

# ---------- 2. releases anteriores (para gerar o delta) ----------
Step 'Baixando a release anterior do GitHub (para o pacote delta)'
# sempre do zero: builds locais antigos (testes sem -Upload) fariam o vpk recusar a versão ou gerar delta contra
# um pacote que não está no GitHub. A base do delta é a última release publicada, baixada logo abaixo.
if (Test-Path $ReleaseDir) { Remove-Item $ReleaseDir -Recurse -Force }
New-Item -ItemType Directory -Force $ReleaseDir | Out-Null
# na primeira versão ainda não existe release (ou nem o repositório): só avisa e segue
$dl = & { $ErrorActionPreference = 'Continue'; vpk download github --repoUrl $RepoUrl --outputDir $ReleaseDir --channel win -x 2>&1 }
if ($LASTEXITCODE -ne 0) {
    $why = $dl | ForEach-Object { "$_" } | Where-Object { $_ -match '\[(ERR|FTL)\]|Exception:' } | Select-Object -First 1
    Write-Host 'Nenhuma release anterior baixada (normal na primeira versão). Seguindo sem delta.' -ForegroundColor Yellow
    if ($why) { Write-Host "  motivo: $($why.Trim())" -ForegroundColor DarkGray }
    $global:LASTEXITCODE = 0
}
else { $dl | ForEach-Object { "$_" } | Where-Object { $_ -match 'INF' } | Select-Object -Last 2 | Write-Host }

# ---------- 3. pack ----------
Step "Empacotando $PackTitle $Version (Setup.exe + MSI)"
vpk pack `
    --packId $PackId `
    --packTitle $PackTitle `
    --packAuthors $PackTitle `
    --packVersion $Version `
    --packDir $PublishDir `
    --runtime win-x64 `
    --channel win `
    --mainExe $MainExe `
    --icon $Icon `
    --msi `
    --instLocation Either `
    --outputDir $ReleaseDir `
    -x
if ($LASTEXITCODE -ne 0) { Fail 'vpk pack falhou.' }

# ---------- 4. upload (opcional) ----------
if ($Upload) {
    Step "Publicando a release v$Version em $RepoUrl"
    try {
        # token só em memória, só durante o upload
        $env:GITHUB_TOKEN = (gh auth token).Trim()
        if (-not $env:GITHUB_TOKEN) { Fail 'gh auth token não retornou um token. Rode: gh auth login' }
        vpk upload github `
            --repoUrl $RepoUrl `
            --token $env:GITHUB_TOKEN `
            --outputDir $ReleaseDir `
            --channel win `
            --publish `
            --releaseName "$PackTitle $Version" `
            --tag "v$Version" `
            --targetCommitish (git -C $Root rev-parse HEAD).Trim() `
            -x
        if ($LASTEXITCODE -ne 0) { Fail 'vpk upload falhou.' }
    }
    finally {
        Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue
    }
}

Step 'Pronto'
Get-ChildItem $ReleaseDir -File | Where-Object { $_.Name -like "*$Version*" -or $_.Extension -in '.exe', '.msi' -or $_.Name -like 'releases.*' } |
    Sort-Object Name | Format-Table Name, @{ n = 'Tamanho (MB)'; e = { '{0:N1}' -f ($_.Length / 1MB) } } -AutoSize
if (-not $Upload) { Write-Host 'Gerado só localmente (sem -Upload).' -ForegroundColor Yellow }
