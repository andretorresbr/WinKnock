<#
.SYNOPSIS
    Instala, atualiza ou remove o serviço WinKnock.

.DESCRIPTION
    Install   : copia os arquivos, registra o serviço e o inicia.
                Se o serviço já existir, funciona como atualização.
    Uninstall : para e remove o serviço, as regras de firewall do grupo
                WinKnock e a pasta de instalação.

    O appsettings.json já existente no destino é preservado, a menos que
    -ReplaceConfig seja informado.

    É obrigatório informar -Source ou -Uninstall.

.EXAMPLE
    .\Install-WinKnock.ps1 -Source C:\Temp\WinKnock

.EXAMPLE
    .\Install-WinKnock.ps1 -Source .

.EXAMPLE
    .\Install-WinKnock.ps1 -Source C:\Temp\WinKnock -ReplaceConfig

.EXAMPLE
    .\Install-WinKnock.ps1 -Uninstall
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    # Pasta com WinKnock.Service.exe e appsettings.json
    [Parameter(ParameterSetName = 'Install')]
    [string]$Source,

    # Substitui o appsettings.json do destino (o antigo é salvo como .bak)
    [Parameter(ParameterSetName = 'Install')]
    [switch]$ReplaceConfig,

    # Não altera a configuração NotifyOnListen do firewall
    [Parameter(ParameterSetName = 'Install')]
    [switch]$SkipFirewallHardening,

    [Parameter(ParameterSetName = 'Uninstall', Mandatory)]
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$ServiceName = 'WinKnock'
$ExeName     = 'WinKnock.Service.exe'
$ConfigName  = 'appsettings.json'

# ProgramW6432 sempre aponta para a pasta de 64 bits, mesmo em PowerShell 32 bits
$programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$InstallDir   = Join-Path $programFiles $ServiceName
$ExePath      = Join-Path $InstallDir $ExeName
$ConfigPath   = Join-Path $InstallDir $ConfigName

# ---------------------------------------------------------------------------
# Funções auxiliares
# ---------------------------------------------------------------------------

function Assert-Administrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Execute este script em um PowerShell como administrador.'
    }
}

function Stop-WinKnock {
    $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Write-Host "Parando o serviço $ServiceName..."
        Stop-Service $ServiceName -Force
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    # Garante que o executável foi liberado antes de substituí-lo
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process -Name 'WinKnock.Service' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $ExePath }) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
}

function Test-ConfigFile([string]$Path) {
    try {
        $json = Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "O arquivo $Path não é um JSON válido: $($_.Exception.Message)"
    }

    $doors = @($json.WinKnock.Doors)
    if ($doors.Count -eq 0 -or $null -eq $doors[0]) {
        throw "O arquivo $Path não contém nenhuma Door em WinKnock.Doors."
    }

    foreach ($d in $doors) {
        Write-Host ("  Door {0}: batidas {1} -> {2} {3} por {4}s" -f `
            $d.Name, ($d.Sequence -join ','), $d.TargetProtocol, $d.TargetPort, $d.OpenDurationSeconds)
    }
}

function Show-RecentEvents {
    try {
        Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $ServiceName } -MaxEvents 10 |
            Format-Table TimeCreated, LevelDisplayName, Message -Wrap | Out-Host
    }
    catch {
        Write-Host 'Nenhum evento do WinKnock encontrado no log Application.'
        Write-Host "Para ver o erro no console, rode: & `"$ExePath`""
    }
}

# ---------------------------------------------------------------------------
# Remoção
# ---------------------------------------------------------------------------

function Invoke-Uninstall {
    Stop-WinKnock

    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        Write-Host "Removendo o serviço $ServiceName..."
        & sc.exe delete $ServiceName | Out-Null
    }

    # O serviço limpa as próprias regras ao parar; isto cobre uma parada forçada
    $rules = Get-NetFirewallRule -Group $ServiceName -ErrorAction SilentlyContinue
    if ($rules) {
        Write-Host "Removendo $(@($rules).Count) regra(s) de firewall do grupo $ServiceName..."
        $rules | Remove-NetFirewallRule
    }

    if (Test-Path $InstallDir) {
        Write-Host "Removendo $InstallDir..."
        Remove-Item $InstallDir -Recurse -Force
    }

    try {
        if ([System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
            [System.Diagnostics.EventLog]::DeleteEventSource($ServiceName)
        }
    }
    catch {
        Write-Verbose "Não foi possível remover a origem de eventos: $($_.Exception.Message)"
    }

    Write-Host 'WinKnock removido.' -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Instalação / atualização
# ---------------------------------------------------------------------------

function Invoke-Install {
    $srcExe    = Join-Path $Source $ExeName
    $srcConfig = Join-Path $Source $ConfigName

    if (-not (Test-Path $srcExe)) {
        throw "Não encontrei $ExeName em '$Source'. Use -Source para indicar a pasta correta."
    }

    $configExists = Test-Path $ConfigPath
    $copyConfig   = $ReplaceConfig -or -not $configExists

    if ($copyConfig) {
        if (-not (Test-Path $srcConfig)) {
            throw "Não encontrei $ConfigName em '$Source'."
        }
        Write-Host "Validando $srcConfig..."
        Test-ConfigFile $srcConfig
    }

    $existing = Get-Service $ServiceName -ErrorAction SilentlyContinue
    Stop-WinKnock

    New-Item $InstallDir -ItemType Directory -Force | Out-Null

    Write-Host "Copiando $ExeName para $InstallDir..."
    Copy-Item $srcExe $InstallDir -Force

    if ($copyConfig) {
        if ($configExists) {
            $backup = "$ConfigPath.$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"
            Copy-Item $ConfigPath $backup
            Write-Host "Configuração anterior salva em $backup"
        }
        Copy-Item $srcConfig $InstallDir -Force
    }
    else {
        Write-Warning "$ConfigName já existe no destino e foi mantido (use -ReplaceConfig para substituir)."
        Write-Host "Validando $ConfigPath..."
        Test-ConfigFile $ConfigPath
    }

    $binPath = "`"$ExePath`""

    if ($existing) {
        Write-Host "Atualizando o registro do serviço $ServiceName..."
        & sc.exe config $ServiceName binPath= $binPath start= auto | Out-Null
    }
    else {
        Write-Host "Registrando o serviço $ServiceName..."
        New-Service -Name $ServiceName `
            -BinaryPathName $binPath `
            -DisplayName 'WinKnock (port knocking)' `
            -Description 'Libera portas no Windows Firewall mediante sequência de batidas UDP.' `
            -StartupType Automatic | Out-Null
    }

    # Reinício automático: 5s, 10s e 60s; contador zera após 1 dia
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null
    # Aplica as ações também quando o serviço para com código de erro
    & sc.exe failureflag $ServiceName 1 | Out-Null

    if (-not $SkipFirewallHardening) {
        # Evita que o pop-up do firewall crie regras por programa que anulam o WinKnock
        Set-NetFirewallProfile -All -NotifyOnListen False
        Write-Host 'Notificações de escuta do firewall desativadas (NotifyOnListen = False).'
    }

    Write-Host "Iniciando o serviço $ServiceName..."
    Start-Service $ServiceName
    Start-Sleep -Seconds 3

    $svc = Get-Service $ServiceName
    if ($svc.Status -ne 'Running') {
        Write-Warning "O serviço não está em execução (status: $($svc.Status)). Eventos recentes:"
        Show-RecentEvents
        exit 1
    }

    Write-Host ''
    Write-Host "WinKnock em execução ($InstallDir)." -ForegroundColor Green
    Get-NetFirewallRule -Group $ServiceName -ErrorAction SilentlyContinue |
        Format-Table DisplayName, Enabled, Action | Out-Host
}

# ---------------------------------------------------------------------------

function Show-Usage {
    $script = Split-Path -Leaf $PSCommandPath
    Write-Host ''
    Write-Host 'Informe o que deseja fazer:' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "  Instalar ou atualizar:   .\$script -Source <pasta> [-ReplaceConfig] [-SkipFirewallHardening]"
    Write-Host "  Remover:                 .\$script -Uninstall"
    Write-Host ''
    Write-Host "  Exemplo (arquivos na pasta atual): .\$script -Source ."
    Write-Host ''
}

if (-not $Uninstall -and [string]::IsNullOrWhiteSpace($Source)) {
    Show-Usage
    exit 1
}

if (-not $Uninstall) {
    if (-not (Test-Path $Source -PathType Container)) {
        Write-Error "A pasta informada em -Source não existe: $Source"
        exit 1
    }
    # Converte caminhos relativos (ex.: ".") em absolutos
    $Source = (Resolve-Path $Source).ProviderPath
}

Assert-Administrator

if ($Uninstall) { Invoke-Uninstall } else { Invoke-Install }
