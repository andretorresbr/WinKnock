# WinKnock

**Port knocking para Windows, usando o Windows Firewall.**

O WinKnock é um serviço do Windows que mantém portas de serviços sensíveis (RDP, SSH, bancos de dados etc.) invisíveis para a rede e só as libera, temporariamente e apenas para o IP de origem, quando recebe uma sequência secreta de "batidas" em portas UDP. É inspirado no [knockd](https://github.com/jvinet/knock), ferramenta consagrada no Linux, mas foi escrito do zero para Windows, em C# e .NET 10, sem drivers de captura de pacotes.

> \[!IMPORTANT]
> Port knocking é **segurança por obscuridade**: uma camada extra que reduz a exposição a varreduras e ataques automatizados. Ele **não substitui** a autenticação do serviço protegido (senhas fortes, chaves SSH, NLA no RDP, MFA). Leia a seção [Limitações e segurança](#limitações-e-segurança).

\---

## Sumário

* [Como funciona](#como-funciona)
* [Arquitetura](#arquitetura)
* [Requisitos](#requisitos)
* [Instalação rápida (executáveis prontos)](#instalação-rápida-executáveis-prontos)
* [Configuração (`appsettings.json`)](#configuração-appsettingsjson)
* [Script de instalação (`Install-WinKnock.ps1`)](#script-de-instalação-install-winknockps1)
* [Usando o cliente](#usando-o-cliente)
* [Abrindo o projeto no Visual Studio](#abrindo-o-projeto-no-visual-studio)
* [Compilando e testando](#compilando-e-testando)
* [Publicando os executáveis](#publicando-os-executáveis)
* [Logs e monitoramento](#logs-e-monitoramento)
* [Solução de problemas](#solução-de-problemas)
* [Limitações e segurança](#limitações-e-segurança)
* [Roadmap](#roadmap)
* [Licença](#licença)

\---

## Como funciona

1. Com o Windows Firewall ativo, toda conexão de entrada sem regra de permissão é descartada em silêncio. A porta protegida (ex.: TCP 3389) aparece como **filtrada** para quem faz varredura.
2. O WinKnock escuta um conjunto de portas **UDP** ("portas de batida"). Ele recebe os datagramas, mas **nunca responde**, então essas portas são indistinguíveis de portas bloqueadas (`open|filtered` no nmap).
3. O cliente envia um datagrama para cada porta da sequência, na ordem certa e dentro do tempo limite (ex.: 7000 → 8000 → 9000 em até 10 segundos).
4. Ao reconhecer a sequência completa, o WinKnock cria no Windows Firewall uma regra de **permissão** para a porta protegida, **restrita ao IP que bateu**.
5. Depois do tempo configurado, a regra é removida automaticamente. Se o mesmo IP bater de novo enquanto a regra existe, o prazo é renovado.

```
 Cliente (203.0.113.10)                         Servidor WinKnock
 ──────────────────────                         ─────────────────────────────────────
 UDP → 7000  ─────────────────────────────────► progresso 1/3   (sem resposta)
 UDP → 8000  ─────────────────────────────────► progresso 2/3   (sem resposta)
 UDP → 9000  ─────────────────────────────────► sequência completa!
                                                 └─ cria regra: Allow TCP 3389
                                                    somente de 203.0.113.10
 TCP → 3389  ═════════════════════════════════► conexão aceita
                                   ... após OpenDurationSeconds ...
                                                 └─ regra removida
```

Como o Windows Firewall é *stateful*, conexões já estabelecidas normalmente continuam funcionando depois que a regra é removida; o prazo controla por quanto tempo **novas** conexões são aceitas.

### Por que UDP?

O knockd captura pacotes na camada de enlace (libpcap) para enxergar tentativas em portas **fechadas**. No Windows, isso exigiria um driver de terceiros (Npcap ou WinDivert). Com TCP, abrir um socket de escuta revelaria a porta, porque o próprio sistema completaria o handshake. Com UDP não há handshake: um socket que recebe e nunca responde é invisível. Isso permite uma implementação em .NET puro, sem drivers.

\---

## Arquitetura

```
WinKnock.sln
├── WinKnock.Core          (net10.0)          Lógica independente de plataforma
│   ├── Configuration/     DoorOptions, WinKnockOptions, validador
│   ├── Engine/            KnockSequenceEngine — máquina de estados das sequências
│   ├── Listener/          UdpKnockListener — sockets UDP de batida
│   └── Access/            DoorAccessManager, IFirewallController, FirewallRuleSpec
├── WinKnock.Firewall      (net10.0-windows)  WindowsFirewallController (COM HNetCfg.FwPolicy2)
├── WinKnock.Service       (net10.0-windows)  Worker Service / Windows Service
├── WinKnock.Client        (net10.0)          knock: envia a sequência de batidas
└── WinKnock.Tests         (net10.0)          Testes xUnit do motor e do gerenciador de acesso
```

```
 ┌───────────────────────┐   batida    ┌──────────────────────┐  sequência   ┌────────────────────┐
 │   UdpKnockListener    │ ──────────► │ KnockSequenceEngine  │ ───────────► │ DoorAccessManager  │
 │ (um socket por porta) │ (IP, porta) │ (estado por IP/Door) │  completada  │ (prazos, renovação)│
 └───────────────────────┘             └──────────────────────┘              └─────────┬──────────┘
                                                                                       │
                                                                          IFirewallController
                                                                                       │
                                                                          ┌────────────▼───────────┐
                                                                          │WindowsFirewallController│
                                                                          │  (COM FwPolicy2)        │
                                                                          └─────────────────────────┘
```

**UdpKnockListener** abre um socket *dual-mode* (IPv4 e IPv6) para cada porta de batida, com `ExclusiveAddressUse` para impedir que outro processo se associe à mesma porta. Nunca envia resposta.

**KnockSequenceEngine** guarda, para cada par (IP de origem, Door), a posição atual na sequência. Uma batida na porta esperada avança; uma batida em outra porta de batida zera o progresso (e recomeça, se for a primeira porta da sequência); o tempo desde a primeira batida é limitado por `SequenceTimeoutSeconds`. O número de entradas rastreadas é limitado para resistir a floods com IPs forjados. O relógio é recebido como parâmetro, o que torna o motor totalmente testável.

**DoorAccessManager** cria a regra de permissão, controla o vencimento, renova o prazo em batidas repetidas e tenta de novo caso a remoção de uma regra falhe.

**WindowsFirewallController** fala com o Windows Firewall pela API COM. Todas as regras criadas ficam no grupo **`WinKnock`**:

|Regra|Quando existe|
|-|-|
|`WinKnock - Portas de batida`|Enquanto o serviço está rodando. Libera as portas UDP de batida **somente para o executável do WinKnock**.|
|`WinKnock - <Door> - <IP>`|Durante o `OpenDurationSeconds` após uma batida correta. Libera a porta protegida **somente para aquele IP**.|

**Worker (serviço)**, ao iniciar: remove regras órfãs do grupo `WinKnock` (de uma queda anterior), avisa no log sobre regras existentes que anulariam a proteção, cria a regra das portas de batida e começa a escutar. A cada segundo revoga acessos vencidos. Ao parar, remove todas as regras que criou. Em caso de falha fatal, encerra com código de erro para que o Windows reinicie o serviço.

\---

## Requisitos

### Para executar

|Item|Requisito|
|-|-|
|Sistema|Windows 10, Windows 11 ou Windows Server (x64)|
|Firewall|Windows Defender Firewall **ativo**, com ação padrão de entrada **Bloquear**|
|Privilégios|Administrador (o serviço roda como LocalSystem)|
|.NET|Não é necessário: os executáveis publicados são *self-contained*|

### Para compilar

|Item|Versão|
|-|-|
|.NET SDK|10.0 (o `global.json` exige 10.0.100 ou superior dentro da linha 10.0)|
|IDE (opcional)|Visual Studio 2026 com o workload **.NET desktop development**, ou VS Code com a extensão **C# Dev Kit**|
|Git|Qualquer versão recente|

O Visual Studio 2022 não tem suporte oficial ao .NET 10.

\---

## Instalação rápida (executáveis prontos)

1. Baixe em [Releases](../../releases) os arquivos `WinKnock-Server-<versão>-win-x64.zip`, `WinKnock-Client-<versão>-win-x64.zip` e `SHA256SUMS.txt`.
2. **Verifique a integridade.** Os pacotes são compilados pelo GitHub Actions e acompanhados de atestado de procedência:

```powershell
   # Compare com o valor listado em SHA256SUMS.txt
   Get-FileHash .\WinKnock-Server-1.0.0-win-x64.zip -Algorithm SHA256

   # Verifica que o arquivo foi gerado por este repositório (requer o GitHub CLI)
   gh attestation verify .\WinKnock-Server-1.0.0-win-x64.zip --repo andretorresbr/WinKnock
   ```

3. **Desbloqueie e extraia** (arquivos baixados recebem uma marca que faz o PowerShell bloquear o script):

```powershell
   Unblock-File .\WinKnock-Server-1.0.0-win-x64.zip
   Expand-Archive .\WinKnock-Server-1.0.0-win-x64.zip -DestinationPath .\WinKnock
   cd .\WinKnock
   ```

4. **Edite o `appsettings.json`.** A sequência de exemplo é pública; defina portas próprias antes de instalar. Veja [Configuração](#configuração-appsettingsjson).
5. **Instale** num PowerShell **como administrador**:

```powershell
   .\Install-WinKnock.ps1 -Source .
   ```

6. **Teste de outra máquina** (o firewall não filtra o tráfego local, então testes em `127.0.0.1` não comprovam a proteção):

```powershell
   Test-NetConnection <IP-do-servidor> -Port 3389     # deve falhar
   .\WinKnock.Client.exe <IP-do-servidor> 7000 8000 9000
   Test-NetConnection <IP-do-servidor> -Port 3389     # deve funcionar
   ```

> \[!NOTE]
> Os executáveis não são assinados digitalmente, então o Windows SmartScreen pode exibir um aviso de "editor desconhecido". A verificação do passo 2 é a forma de confirmar a origem dos arquivos.

\---

## Configuração (`appsettings.json`)

O arquivo fica ao lado do executável (em `%ProgramFiles%\WinKnock` após a instalação). Cada **Door** é uma porta protegida com sua própria sequência de batidas, equivalente a uma seção do `knockd.conf`.

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information",
      "WinKnock": "Information"
    },
    "EventLog": {
      "LogLevel": {
        "Default": "Warning",
        "WinKnock": "Information"
      }
    }
  },
  "WinKnock": {
    "Doors": [
      {
        "Name": "RDP",
        "Sequence": [ 7000, 8000, 9000 ],
        "SequenceTimeoutSeconds": 10,
        "TargetPort": 3389,
        "TargetProtocol": "Tcp",
        "OpenDurationSeconds": 30
      }
    ]
  }
}
```

### Campos de cada Door

|Campo|Descrição|Regras|
|-|-|-|
|`Name`|Identificador usado nos logs e no nome da regra de firewall|1 a 40 caracteres: letras, números, `\_` ou `-`; único|
|`Sequence`|Portas UDP a serem batidas, **na ordem**|Mínimo de 2 portas, entre 1 e 65535; não pode repetir a sequência de outra Door|
|`SequenceTimeoutSeconds`|Tempo máximo entre a primeira e a última batida|1 a 300 (padrão 10)|
|`TargetPort`|Porta do serviço a ser liberada|1 a 65535|
|`TargetProtocol`|`Tcp` ou `Udp`|Se `Udp`, a porta não pode fazer parte da sequência|
|`OpenDurationSeconds`|Por quanto tempo a regra fica ativa|1 a 86400 (padrão 30)|

Se a configuração for inválida, o serviço **não inicia** e registra os erros encontrados.

### Várias Doors

```json
"Doors": [
  { "Name": "RDP", "Sequence": [ 41234, 17771, 30512, 22001 ], "TargetPort": 3389, "TargetProtocol": "Tcp" },
  { "Name": "SSH", "Sequence": [ 52011, 12877, 44120, 9131 ],  "TargetPort": 22,   "TargetProtocol": "Tcp" }
]
```

### Boas práticas para a sequência

* Use **3 a 5 portas** altas e aleatórias, sem padrão óbvio (evite 1000, 2000, 3000).
* Evite portas usadas por outros programas; o serviço não inicia se alguma já estiver ocupada.
* Não repita portas na sequência (`7000, 7000, 8000`): um datagrama duplicado pela rede seria contado como duas batidas, e a repetição reduz a variedade da sequência.
* Mantenha o arquivo de produção **fora do controle de versão**.

### Alterando a configuração

O arquivo é lido na inicialização. Após editar, reinicie o serviço:

```powershell
Restart-Service WinKnock
```

\---

## Script de instalação (`Install-WinKnock.ps1`)

O script instala, atualiza e remove o serviço. Precisa ser executado como administrador, e **exige** que você informe `-Source` ou `-Uninstall`.

```powershell
# Instalar ou atualizar (arquivos na pasta atual)
.\\Install-WinKnock.ps1 -Source .

# Instalar a partir de outra pasta
.\\Install-WinKnock.ps1 -Source D:\Downloads\WinKnock

# Atualizar substituindo também a configuração (a anterior é salva como .bak)
.\\Install-WinKnock.ps1 -Source . -ReplaceConfig

# Instalar sem alterar as notificações do firewall
.\\Install-WinKnock.ps1 -Source . -SkipFirewallHardening

# Remover completamente
.\\Install-WinKnock.ps1 -Uninstall
```

### O que ele faz na instalação/atualização

1. Valida o `appsettings.json` e lista as Doors configuradas.
2. Para o serviço, se estiver rodando, e espera o executável ser liberado.
3. Copia os arquivos para `%ProgramFiles%\\WinKnock` (usa `ProgramW6432`, então funciona mesmo em PowerShell 32 bits e em instalações com Program Files em outra unidade).
4. **Preserva** um `appsettings.json` já existente, a menos que `-ReplaceConfig` seja usado.
5. Registra o serviço `WinKnock` com inicialização automática (ou atualiza o registro, se já existir).
6. Configura reinício automático em caso de falha (após 5 s, 10 s e 60 s), inclusive quando o serviço sai com código de erro.
7. Desativa as notificações de escuta do firewall (`NotifyOnListen = False`). Veja o motivo em [Solução de problemas](#o-pop-up-do-firewall-criou-regras-para-um-programa).
8. Inicia o serviço e mostra as regras do grupo `WinKnock`; se o serviço não subir, exibe os eventos recentes.

### O que ele faz na remoção

Para e remove o serviço, apaga as regras de firewall do grupo `WinKnock`, remove a pasta de instalação e a origem de eventos.

### Por que `Program Files`

O serviço roda como **LocalSystem** e altera o firewall. Instalado numa pasta em que usuários comuns podem gravar, qualquer um poderia trocar o executável ou a configuração e obter privilégios de sistema. `Program Files` só permite escrita a administradores.

### Política de execução

Se o PowerShell bloquear o script:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-WinKnock.ps1 -Source .
```

\---

## Usando o cliente

```
WinKnock.Client.exe <host> <porta1> [porta2 ...] [--delay ms]
```

|Parâmetro|Descrição|
|-|-|
|`host`|Nome ou IP do servidor (IPv4 tem preferência quando o nome resolve para ambos)|
|`portaN`|Portas da sequência, na ordem|
|`--delay`|Intervalo entre batidas em milissegundos (padrão 200). UDP não garante a ordem de entrega; o intervalo evita que as batidas cheguem trocadas|

Exemplos:

```powershell
WinKnock.Client.exe servidor.exemplo.com 41234 17771 30512 22001
WinKnock.Client.exe 10.0.0.62 41234 17771 30512 22001 --delay 500

# Bater e já conectar via RDP
WinKnock.Client.exe 10.0.0.62 41234 17771 30512 22001; mstsc /v:10.0.0.62
```

### Sem o cliente

Qualquer ferramenta que envie datagramas UDP serve.

**PowerShell:**

```powershell
$u = \[System.Net.Sockets.UdpClient]::new()
foreach ($p in 41234, 17771, 30512, 22001) {
    \[void]$u.Send(\[byte\[]]@(0), 1, "10.0.0.62", $p)
    Start-Sleep -Milliseconds 200
}
$u.Close()
```

**Linux/macOS:**

```bash
for p in 41234 17771 30512 22001; do echo -n x | nc -u -w1 10.0.0.62 $p; done
ssh usuario@10.0.0.62
```

O cliente `knock` do pacote knockd também funciona, usando o modo UDP: `knock -u 10.0.0.62 41234 17771 30512 22001`.

\---

## Abrindo o projeto no Visual Studio

### Clonando pelo Visual Studio

1. Abra o **Visual Studio 2026**.
2. Na tela inicial, escolha **Clone a repository**.
3. Em **Repository location**, informe `https://github.com/andretorresbr/WinKnock.git`, escolha a pasta local e clique em **Clone**.
4. Se o Visual Studio abrir em modo de pasta, use **File → Open → Project/Solution** e selecione `WinKnock.sln`. Prefira sempre abrir a solution: o modo de pasta não mostra a estrutura de projetos nem o Test Explorer.

### Clonando pela linha de comando

```powershell
git clone https://github.com/andretorresbr/WinKnock.git
cd WinKnock
start WinKnock.sln
```

### Conferindo o ambiente

```powershell
dotnet --list-sdks
```

Deve haver um SDK 10.0.x. Se não houver, instale o [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) ou, no **Visual Studio Installer**, o workload **.NET desktop development**.

No Solution Explorer devem aparecer os cinco projetos. Se o IntelliSense mostrar erros que a compilação não mostra, feche o Visual Studio, apague a pasta oculta `.vs` e as pastas `bin`/`obj`, e abra novamente.

\---

## Compilando e testando

### Pelo Visual Studio

* **Compilar:** Build → Rebuild Solution (`Ctrl+Shift+B`).
* **Testar:** Test → Test Explorer → Run All (`Ctrl+R, A`).
* **Executar o serviço em modo console:**

  1. Abra o Visual Studio **como administrador** (necessário para alterar o firewall).
  2. Botão direito em **WinKnock.Service** → **Set as Startup Project**.
  3. Pressione `F5`.
  4. Pare com `Ctrl+C` na janela do console. O botão **Stop** do Visual Studio encerra o processo à força e as regras ficam para trás; elas são removidas na próxima inicialização.

### Pela linha de comando

```powershell
dotnet build
dotnet test

# Em um terminal como administrador
dotnet run --project WinKnock.Service

# Em outro terminal
dotnet run --project WinKnock.Client -- 127.0.0.1 7000 8000 9000
```

Para ver cada batida recebida durante o desenvolvimento, ajuste o nível de log no `appsettings.json`:

```json
"LogLevel": { "WinKnock": "Debug" }
```

### Ambiente de teste recomendado

Nunca teste em cima do seu único acesso remoto à máquina. Use uma porta de teste (ex.: 5555) com um listener simples:

```powershell
$l = \[System.Net.Sockets.TcpListener]::new(\[ipaddress]::Any, 5555); $l.Start()
"Escutando na 5555"; $c = $l.AcceptTcpClient(); "Conexão de $($c.Client.RemoteEndPoint)"
$c.Close(); $l.Stop()
```

E teste a partir de **outra máquina**, já que o Windows Firewall não filtra o loopback.

\---

## Publicando os executáveis

```powershell
# Serviço: arquivo único, self-contained, comprimido (\~35 MB)
dotnet publish WinKnock.Service -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish\\service

# Cliente: arquivo único, self-contained, com trimming
dotnet publish WinKnock.Client -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -o publish\\client
```

O serviço **não** usa trimming: o acesso ao firewall via COM com `dynamic` não é compatível com ele.

Alternativa menor para o serviço, dependente do .NET 10 Runtime instalado na máquina (recebe correções de segurança do runtime pelo Microsoft Update, sem republicar):

```powershell
dotnet publish WinKnock.Service -c Release -r win-x64 --no-self-contained `
    -p:PublishSingleFile=true -o publish\\service
```

A pasta `publish` é ignorada pelo Git.

### Releases automatizadas

O workflow [`.github/workflows/release.yml`](.github/workflows/release.yml) roda ao enviar uma tag `vX.Y.Z`: executa os testes, publica os dois executáveis, gera os zips, o `SHA256SUMS.txt`, o atestado de procedência e cria a release.

```powershell
git tag v1.0.1
git push origin v1.0.1
```

Como os executáveis são *self-contained*, cada atualização de segurança mensal do .NET pede uma nova release.

\---

## Logs e monitoramento

Rodando como serviço, o WinKnock registra no **Visualizador de Eventos → Logs do Windows → Aplicativo**, com a origem `WinKnock`: acessos liberados, renovados e revogados, avisos de configuração e erros.

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'WinKnock' } -MaxEvents 20 |
    Format-Table TimeCreated, LevelDisplayName, Message -Wrap
```

Regras ativas no momento:

```powershell
Get-NetFirewallRule -Group WinKnock | Format-Table DisplayName, Enabled, Action
```

Detalhes de uma regra de acesso:

```powershell
$r = Get-NetFirewallRule -DisplayName "WinKnock - RDP - 203.0.113.10"
$r | Get-NetFirewallPortFilter    | Format-List Protocol, LocalPort
$r | Get-NetFirewallAddressFilter | Format-List RemoteAddress
```

Para acompanhar o que o firewall permite e descarta:

```powershell
Set-NetFirewallProfile -All -LogAllowed True -LogBlocked True
Get-Content "$env:SystemRoot\\System32\\LogFiles\\Firewall\\pfirewall.log" -Wait | Select-String " 3389 "

# Ao terminar
Set-NetFirewallProfile -All -LogAllowed False -LogBlocked False
```

\---

## Solução de problemas

### A porta protegida está acessível sem batida

Alguma regra está permitindo o acesso. O WinKnock só protege portas que **nenhuma outra regra** libera. Na inicialização, o serviço registra avisos de "regra conflitante"; leia-os. Causas comuns:

* **Regras nativas do serviço protegido**, como "Área de Trabalho Remota" (RDP) ou "OpenSSH SSH Server". Desative-as:

```powershell
  Get-NetFirewallRule -DisplayGroup "Área de Trabalho Remota" | Disable-NetFirewallRule   # Windows em português
  Get-NetFirewallRule -DisplayGroup "Remote Desktop"          | Disable-NetFirewallRule   # Windows em inglês
  ```

* **Regras por programa**, que liberam qualquer porta para um executável. Procure-as:

```powershell
  Get-NetFirewallApplicationFilter -PolicyStore ActiveStore |
      Where-Object Program -match 'nome-do-programa' |
      Get-NetFirewallRule | Format-Table DisplayName, Action, Enabled, Profile
  ```

* **Ação padrão de entrada** diferente de Bloquear:

```powershell
  Get-NetFirewallProfile -PolicyStore ActiveStore | Format-Table Name, Enabled, DefaultInboundAction
  ```

* **Regras de GPO** em máquinas de domínio. Use sempre `-PolicyStore ActiveStore` para enxergá-las.

### A batida funciona, a regra é criada, mas a conexão não passa

No Windows Firewall, **regras de bloqueio vencem regras de permissão**. Procure bloqueios que atinjam a porta ou o programa do serviço:

```powershell
Get-NetFirewallRule -PolicyStore ActiveStore -Direction Inbound -Enabled True -Action Block |
    ForEach-Object {
        $pf  = $\_ | Get-NetFirewallPortFilter
        $app = $\_ | Get-NetFirewallApplicationFilter
        \[pscustomobject]@{ Regra = $\_.DisplayName; Porta = ($pf.LocalPort -join ','); Programa = $app.Program }
    } | Format-Table -AutoSize
```

Não crie regras de bloqueio para a porta protegida: o bloqueio padrão do firewall já cumpre esse papel, e uma regra explícita impediria o WinKnock de liberar o acesso.

### O pop-up do firewall criou regras para um programa

Quando um programa começa a escutar numa porta pela primeira vez, o Windows pergunta se deve permitir o acesso. Clicar em **Permitir** cria uma regra que libera **qualquer porta** para o programa (anulando o WinKnock); clicar em **Cancelar** cria regras de **bloqueio** (impedindo o acesso mesmo após a batida). O script de instalação desativa essas notificações; para fazer manualmente:

```powershell
Set-NetFirewallProfile -All -NotifyOnListen False
```

Depois, remova as regras criadas para o programa afetado. Com as notificações desativadas, programas que precisarem receber conexões exigirão regras criadas manualmente.

### As batidas não chegam ao servidor

* Confirme que a regra `WinKnock - Portas de batida` existe enquanto o serviço roda.
* Procure regras de bloqueio para `WinKnock.Service.exe`.
* Verifique firewalls de rede, roteadores ou provedores de nuvem entre o cliente e o servidor (security groups, NSGs): as portas UDP de batida precisam estar liberadas neles.
* Algumas redes corporativas bloqueiam saída UDP em portas altas.
* Rode o serviço em modo console com log `Debug` para ver cada batida recebida.

### A sequência às vezes falha

Aumente o intervalo entre batidas (`--delay 500`) e confira se `SequenceTimeoutSeconds` comporta a sequência inteira. Em redes com NAT ou múltiplas saídas, garanta que todas as batidas e a conexão final saiam pelo mesmo IP público.

### O serviço não inicia

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'WinKnock' } -MaxEvents 10 |
    Format-Table TimeCreated, Message -Wrap

# Ou execute diretamente, como administrador, para ver o erro no console
\& "$env:ProgramFiles\\WinKnock\\WinKnock.Service.exe"
```

As causas mais comuns são erro de validação no `appsettings.json` e porta de batida já ocupada por outro programa (`netstat -ano -p udp | findstr :7000`).

### Testes em localhost não refletem a proteção

O Windows Firewall não filtra o tráfego de loopback. Testes em `127.0.0.1` mostram que a regra é criada, mas não que a porta estava bloqueada antes. Teste sempre a partir de outra máquina.

\---

## Limitações e segurança

* **Replay.** As batidas trafegam sem criptografia. Quem observa a rede entre cliente e servidor pode capturar e repetir a sequência. Use o WinKnock como camada adicional, nunca como única proteção.
* **NAT compartilhado.** A regra libera o IP público de origem; outros dispositivos atrás do mesmo NAT também ganham acesso durante o prazo.
* **Negação de serviço.** Um flood com IPs forjados pode ocupar a tabela de progresso (limitada para proteger a memória) e atrasar clientes legítimos até as entradas expirarem.
* **Sequência de exemplo.** A sequência `7000, 8000, 9000` deste repositório é pública. **Nunca a use em produção.**
* **Dependência do firewall.** A proteção só existe se nenhuma outra regra liberar a porta e se o Windows Firewall estiver ativo. Softwares de segurança que substituem o Windows Firewall não são suportados.
* **Serviço privilegiado.** O WinKnock roda como LocalSystem e processa pacotes vindos da rede. O processamento é mínimo (apenas IP de origem e porta; o conteúdo é ignorado), mas mantenha o executável atualizado com as correções mensais do .NET.
* **Sem proteção de conteúdo.** O WinKnock controla quem pode tentar se conectar; ele não criptografa nem autentica a sessão do serviço protegido.

Para relatar uma vulnerabilidade, use a aba **Security → Report a vulnerability** deste repositório em vez de abrir uma issue pública.

\---

## Roadmap

* \[ ] Recusar a sequência de exemplo na validação.
* \[ ] Campo `ServiceProgram` na Door, para avisar sobre regras de permissão ou bloqueio vinculadas ao executável do serviço protegido.
* \[ ] Aviso na inicialização quando `NotifyOnListen` estiver ativado.
* \[ ] Sequências de uso único (`one\_time\_sequences` do knockd).
* \[ ] Modo **SPA** (*Single Packet Authorization*): um único datagrama com HMAC e timestamp, resistente a replay.
* \[ ] Controller de firewall com interfaces COM tipadas, permitindo trimming e executáveis menores.
* \[ ] Assinatura digital dos executáveis.

\---

## Licença

Distribuído sob a licença MIT. Veja [LICENSE.txt](LICENSE.txt).

Inspirado no [knockd](https://github.com/jvinet/knock), de Judd Vinet.

