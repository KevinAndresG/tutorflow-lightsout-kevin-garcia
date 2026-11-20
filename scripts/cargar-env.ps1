# Carga las variables de .env (raiz del repo) en el proceso actual.
# Uso: . .\scripts\cargar-env.ps1
# No imprime valores: solo los nombres definidos y si LLM_API_KEY quedo definida.
$archivo = Join-Path (Split-Path $PSScriptRoot -Parent) '.env'
$definidas = @()
if (Test-Path $archivo) {
    foreach ($linea in Get-Content -LiteralPath $archivo) {
        $t = $linea.Trim()
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        $i = $t.IndexOf('=')
        if ($i -lt 1) { continue }
        $nombre = $t.Substring(0, $i).Trim()
        $valor = $t.Substring($i + 1).Trim().Trim('"').Trim("'")
        if ($valor -eq '') { continue }
        Set-Item -Path "Env:$nombre" -Value $valor
        $definidas += $nombre
    }
} else {
    Write-Host 'No existe .env en la raiz.'
}
Write-Host ('Variables definidas: ' + ($definidas -join ', '))
Write-Host ('LLM_API_KEY definida: ' + $(if ($env:LLM_API_KEY) { 'si' } else { 'no' }))
