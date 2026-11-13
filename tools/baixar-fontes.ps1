# Baixa as fontes do Helmet Desk pela API do Google Fonts e salva em src\Assets\Fonts.
# O app embute os .ttf no executável, então só é preciso rodar isto para trocar ou
# atualizar as fontes. Depois, recompile.
#
#   powershell -ExecutionPolicy Bypass -File tools\baixar-fontes.ps1
#
# Geo (nome do app) e Roboto (resto da interface) — ambas sob a SIL Open Font License 1.1.

$ErrorActionPreference = 'Stop'
$dest = Join-Path $PSScriptRoot '..\src\Assets\Fonts'
New-Item -ItemType Directory -Force $dest | Out-Null

# Pedido no formato "família:ital,wght@...". Um User-Agent antigo faz o Google devolver
# .ttf inteiros (navegadores modernos recebem woff2 fatiado, que o WPF não lê).
$css = 'https://fonts.googleapis.com/css2?family=Geo:ital@0;1' +
       '&family=Roboto:ital,wght@0,300;0,400;0,500;0,600;0,700;0,800;1,400;1,500;1,700'
$text = (Invoke-WebRequest -UseBasicParsing -Uri $css -UserAgent 'Mozilla/4.0').Content

$weights = @{ 300='Light'; 400='Regular'; 500='Medium'; 600='SemiBold'; 700='Bold'; 800='ExtraBold' }
$re = "font-family: '(?<fam>[^']+)';\s*font-style: (?<style>\w+);\s*font-weight: (?<w>\d+);[^}]*?url\((?<url>[^)]+\.ttf)\)"
$found = [regex]::Matches($text, $re)
if ($found.Count -eq 0) { throw 'O Google Fonts não devolveu nenhum .ttf.' }

foreach ($m in $found) {
    $fam = $m.Groups['fam'].Value -replace ' ', ''
    $w = [int]$m.Groups['w'].Value
    $name = $weights[$w]
    if ($m.Groups['style'].Value -eq 'italic') { $name = if ($name -eq 'Regular') { 'Italic' } else { "${name}Italic" } }
    $file = Join-Path $dest "$fam-$name.ttf"
    Invoke-WebRequest -UseBasicParsing -Uri $m.Groups['url'].Value -OutFile $file
    Write-Host ("{0,-28} {1,8:N0} bytes" -f (Split-Path $file -Leaf), (Get-Item $file).Length)
}

# Licença das duas famílias (repositório oficial do Google Fonts)
Invoke-WebRequest -UseBasicParsing -Uri 'https://raw.githubusercontent.com/google/fonts/main/ofl/geo/OFL.txt' -OutFile (Join-Path $dest 'Geo-OFL.txt')
Invoke-WebRequest -UseBasicParsing -Uri 'https://raw.githubusercontent.com/google/fonts/main/ofl/roboto/OFL.txt' -OutFile (Join-Path $dest 'Roboto-OFL.txt')
Write-Host 'Pronto.'
