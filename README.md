# Star Citizen Mining Assistant by Vorxen

Overlay para Windows que **lê a tela** durante a mineração com Prospector/MOLE, reconhece o **RS (radar signature)** e mostra qual minério provavelmente é — incluindo **quantas rochas** há no cluster (RS = RS base × nº de rochas).

```
┌──────────────────────┐
│ ⛏ MINING ASSISTANT · by Vorxen │
│ GOLD                 │
│ RS 10,755            │
│ 3 ROCKS              │
│ ★★ VERY HIGH PRIORITY│
└──────────────────────┘
```

> **Copiloto visual, não um bot.** O app só observa a tela exatamente como você vê. Ele **não** injeta DLL, **não** lê memória, **não** modifica arquivos do jogo, **não** intercepta rede, **não** faz hook e **não** envia nenhuma tecla/mouse ao jogo. Todo processamento (captura + OCR) é local; nada é enviado para a internet. Veja [Segurança e anti-cheat](#segurança-e-anti-cheat-easy-anti-cheat).

---

## Status (MVP 0.1)

| Recurso | Status |
|---|---|
| Captura de tela (só das ROIs) | ✅ |
| ROI configurável (calibração com mouse, normalizada por resolução) | ✅ |
| OCR do RS (Windows.Media.Ocr, local) | ✅ |
| Normalização OCR (`358S`→3585, `3B70`→3870, `14,340`→14340) | ✅ |
| `materials.json` externo + databases por versão | ✅ |
| Detecção de múltiplos (1–12 rochas) | ✅ |
| Confiança / Exact · Probable · Ambiguous · Unknown | ✅ |
| Votação temporal (maioria de N frames) | ✅ |
| Overlay transparente, click-through, always-on-top, fora do Alt+Tab | ✅ |
| Hotkeys globais | ✅ |
| Save Unknown Signature | ✅ |
| Target materials (filtro) + alertas sonoros | ✅ |
| Debug Mode + Analyze Screenshot + CLI de regressão | ✅ |
| Scan detalhado (Mass/Resistance/Instability/Composition) | ⏳ próxima fase |
| Prospector difficulty analyzer / loadouts.json | ⏳ |
| Histórico/CSV | ⏳ |

---

## Instalação

### Requisitos
- Windows 10 2004+ / Windows 11, x64
- Resolução do jogo de **1080p ou maior** (abaixo disso os dígitos ficam pequenos demais para ler com confiança)
- Star Citizen em **Borderless Windowed** ou **Fullscreen Windowed**
- Pacote OCR de inglês do Windows (normalmente já instalado; veja abaixo)
- Só para a versão `publish\`: [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). A versão `dist\` não precisa de nada instalado.

### Gerar os pacotes

```bash
powershell -ExecutionPolicy Bypass -File publish.ps1
```

| Saída | Para quê |
|---|---|
| `publish\MiningAssistant.exe` | uso neste PC (pequeno; precisa do .NET 8 Desktop Runtime) |
| `dist\MiningAssistant\MiningAssistant.exe` | autocontido: roda em qualquer Windows 10/11 x64 sem instalar nada |
| `dist\MiningAssistant.zip` | a pasta acima zipada, sem configurações locais, para copiar para **outro computador** |

### Em outro computador

1. Descompacte `MiningAssistant.zip` em qualquer pasta e abra `MiningAssistant.exe`. O painel de controle abre na hora.
2. Se algo não funcionar, use **Advanced settings → Run self-test** (ou rode `MiningAssistant.exe --selftest`). O relatório `config\selftest.txt` testa pastas, banco de dados, monitores (mínimo 1080p), captura de tela e OCR, e diz o que falta.
3. Abrir o `.exe` de novo com o app já rodando traz o painel para a frente; antes, parecia que "nada acontecia".
4. Erros de inicialização aparecem numa mensagem e ficam em `config\crash.log`.

### Ativação (key por nome + PC)

Na primeira vez o app pede **nome** e **key**. A key vale para aquele nome **naquele PC**: copiar a pasta instalada (ou passar a key) para outro computador não ativa o app.

1. O jogador abre o app e clica **Copy name + code**: copia o nome e o **código do PC** (`XXXX-XXXX`) para mandar ao Vorxen.
2. Vorxen gera a key:
   ```bash
   dotnet run --project src/MiningAssistant.Cli -- keygen "Nome do Jogador" XXXX-XXXX
   ```
3. O jogador digita a key e clica **ACTIVATE** (só uma vez neste PC).

O código do PC é um hash do identificador da instalação do Windows (`MachineGuid`, lido localmente, nunca enviado). Ele muda se o Windows for reinstalado; aí o jogador precisa de uma key nova. Keys antigas, só por nome (até a v0.1.2), não são mais aceitas.

### Compilar e testar

```bash
dotnet build -c Release
dotnet test
```

---

## Painel de controle

O painel abre junto com o app (também pela bandeja ou abrindo o `.exe` de novo):

- **▶ START / ■ STOP**: START faz uma **contagem regressiva de 5 a 1** no centro da tela, com o aviso para trocar para a janela do Star Citizen, e só então começa a ler a tela. Clicar durante a contagem cancela. Quando a leitura começa, o painel minimiza sozinho, porque ele abre exatamente onde o código RS aparece.
- **⟳ Reload**: relê `settings.json`, a calibração e `materials.json` do disco e recria scanner, OCR, atalhos e overlay, sem fechar o app.
- **Restart app**: fecha e abre o app de novo.
- **Funções (ON/OFF)**: overlay, localização automática da placa, atalhos, alertas sonoros, só alvos, salvar capturas ilegíveis, modo debug, esconder overlay de gravações, abrir o painel ao iniciar, iniciar sem contagem, minimizar o painel ao iniciar.
- **Ao vivo**: miniatura do que o app está capturando, com a placa encontrada contornada em verde, a leitura atual, o valor estável e o resultado.
- **Checklist**: OCR, banco, área de busca, resolução, captura, atalhos e se alguma janela do app está cobrindo a área de busca, com ✓/✗ e o motivo.
- **Advanced settings**: versão do banco, monitor, contexto, intervalo, tolerância, votação, alvos, posição do overlay, pastas e o autoteste.

---

## Como o código RS é encontrado e lido

O indicador RS do Star Citizen é uma **placa âmbar com um ícone de pin e o número em branco** (ex. `📍 15,420`), logo acima do retículo central. Ele **balança** com o movimento da nave, então o app não depende de uma posição fixa:

1. Captura a **área de busca** (padrão: o terço central da tela).
2. **Localiza o ícone de pin**: pixels claros com pouco azul (o ícone e as franjas vermelha/verde da aberração cromática do HUD), com o tamanho certo para a resolução (≈1,6% da altura da tela) e a **silhueta de pin** (anel em cima, cintura estreita, base larga).
3. Confere que, à direita do ícone e a ≈0,3× a altura dele, há **dígitos brancos** com a altura e largura esperadas. Texto âmbar do HUD (ex. "5.0km") e pontos ciano do scanner são rejeitados pela cor.
4. Rejeita placas **cortadas pela borda**: melhor não ler do que ler meio número.
5. Lê **só os dígitos**, pelo canal verde (o único sem deslocamento de aberração cromática), apagando o que aparece através da placa semitransparente (asteroides, pontos do scanner, chão claro), e amplia os dígitos para ≈50 px de altura, o tamanho que o OCR do Windows lê melhor.

Se a área calibrada for pequena demais, ela é ampliada automaticamente para pelo menos 30% × 30% da tela em volta do mesmo centro (`ocr.minSearchWidth/minSearchHeight`).

Casos reais que já falharam viraram testes de regressão: `test_images/live` (asteroide encostado na placa, pontos do scanner atrás dela, chão claro de Daymar) e `test_images/negatives` (o "5.0km" que já foi confundido com a placa).

## Configurar o OCR

O app usa o OCR embutido do Windows (`Windows.Media.Ocr`): rápido, local, sem downloads extras. Ele precisa do componente OCR de um idioma. Para verificar:

```bash
dotnet run --project src/MiningAssistant.Cli -- ocr-langs
```

Se `en-US` não aparecer, instale (PowerShell **como administrador**):

```powershell
Add-WindowsCapability -Online -Name "Language.OCR~~~en-US~0.0.1.0"
```

Se o idioma configurado não existir, o app usa os idiomas do seu perfil (pt-BR também lê números). O idioma fica em `config/settings.json` → `ocr.language`.

---

## Área de busca (calibração)

O app já vem com uma área de busca pronta (`data/profiles/star-citizen-center.roi.json`: o terço central da tela) e na maioria dos casos **não precisa calibrar**. Para mudar: **Calibrate search area…** no painel (ou `Ctrl+Shift+C`), escolha *RS search area* e desenhe uma caixa **grande** em volta de onde o código aparece. Quanto maior, mais tolerante ao balanço do HUD. **Enter** salva, **Esc** cancela.

As regiões ficam salvas como porcentagem do monitor (`config/profiles/default.roi.json`), então se adaptam a outras resoluções. Escolha o monitor do jogo em *Advanced settings → Game monitor*.

---

## Usando

| Atalho | Ação |
|---|---|
| **Page Up** | mostrar/esconder overlay |
| **Page Down** | mover o overlay: arraste o cartão e aperte de novo (ou duplo clique) para travar |
| **/** | **snapshot de depuração** deste instante (pasta `debug_snapshots\`) |
| **Ctrl+Shift+S** | escanear agora |
| **Ctrl+Shift+P** | ligar/desligar a leitura (sem contagem) |
| **Ctrl+Shift+C** | calibrar a área de busca |
| **Ctrl+Shift+D** | janela de debug |
| **Ctrl+Shift+X** | contexto Auto → Space → Surface |

Os atalhos são só "ouvidos" (Raw Input passivo), nunca bloqueados, então o jogo também recebe as teclas. `/` é reconhecido pelo caractere (funciona em teclado ABNT2 e US). Até a v0.1.4 os padrões eram `[` e `]`, que o Star Citizen usa para aceitar/recusar missões; configurações antigas com os padrões passam sozinhas para Page Up (overlay), Page Down (mover) e `/` (snapshot). Se o contador "keys received" do painel não subir com o jogo em foco, o jogo está rodando como administrador: tire o modo administrador do RSI Launcher. Edite os atalhos em `config/settings.json` → `hotkeys` e clique **Reload**.

**Mover o overlay:** aperte **Page Down** (ou *Move overlay* no painel/bandeja), libere o mouse do jogo (`Alt+Tab`), arraste o cartão e aperte **Page Down** de novo. A posição fica salva.

**Snapshot de depuração (`/`):** numa situação em que a leitura falha (ou dá errado), aperte **/** (ou *📸 Debug snapshot* no painel, na janela de debug ou na bandeja). Na hora, o app grava uma pasta `debug_snapshots\AAAAMMDD_HHMMSS_mmm\` com:

| Arquivo | Conteúdo |
|---|---|
| `1_screen.png` | o monitor do jogo inteiro naquele instante |
| `2_search_area.png` / `2_search_area_marked.png` | a área de busca, e a mesma com o ícone (verde) e os dígitos (vermelho) encontrados |
| `3_plate.png` | o recorte da placa RS, se encontrada |
| `4_ocr_NN_*.png` | cada imagem enviada ao OCR |
| `report.txt` | por que cada candidato foi aceito ou rejeitado, o que o OCR leu em cada imagem, a leitura e o resultado finais, o que o overlay mostrava e as configurações |

Cada snapshot tem ~4 MB e fica só no seu PC. Mande a pasta (ou o `1_screen.png`) para análise. `mining-cli snapshot <1_screen.png>` refaz toda a análise de um snapshot depois de qualquer ajuste no app.

**Leituras perdidas automáticas:** se a placa RS é encontrada mas não lida, ou surge um RS desconhecido, o app também salva sozinho o recorte da placa em `diagnostics/missed/` (no máximo 1 a cada 3 s, até 300 arquivos).

### Assinaturas desconhecidas: ensinar o app

Quando aparece um RS que nada no banco explica, abre uma **janelinha persistente** à direita da tela. Ela não some junto com o cartão do overlay e não rouba o foco do jogo; várias desconhecidas entram em fila nela. Você pode:

- **✓ Save**: informar o que é (nome; quantas rochas ou depósitos; tipo: minério, asteroide, depósito FPS/ROC, salvage; notas). O app guarda em `config/learned_signatures.json`, com o recorte da placa em `config/learned/`, e **passa a reconhecer aquele RS na hora**, inclusive os múltiplos;
- **Ignore**: fecha sem guardar (pergunta de novo em outra sessão);
- **Never ask for this RS**: nunca mais pergunta por aquele número.

Desligue a janela em *Functions → Ask me to name unknown signatures*.

**Enviar para o repositório (opcional):** com *Functions → Send named signatures to GitHub* ligado (pede confirmação) e a caixa *Send…* marcada na janelinha, a assinatura vai para `contributions/` no repositório privado: só os valores digitados e o recorte da placa, **nunca a tela inteira**. O envio é feito pelo **GitHub CLI (`gh`)** logado neste PC. O app em si não abre conexão de rede, não usa scripts codificados e não guarda senha ou token; o login fica no Gerenciador de Credenciais do Windows, gerenciado pelo `gh`. Isso evita que o antivírus ou o firewall tratem o envio como suspeito. Se falhar (sem `gh`, sem internet), a assinatura fica salva como pendente: *Advanced settings → Send pending to GitHub*. Para preparar outro PC: `winget install GitHub.cli` e `gh auth login`.
### O que o overlay mostra

| Situação | Overlay |
|---|---|
| Nada detectado | nada (ou a pílula "⛏ scanning", se ligada) |
| RS exato | **GOLD** · RS 3,585 · 1 ROCK · ★★ VERY HIGH PRIORITY |
| RS próximo (ex.: 3584) | **GOLD** · … · Confidence 97% |
| Dois candidatos parecidos | **⚠ POSSIBLE:** GOLD ×6 · ICE ×5 com as porcentagens |
| RS desconhecido | **? UNKNOWN** · RS 5,001 |
| Filtro de alvos ligado e achou | faixa **◎ TARGET FOUND** |

Cores por prioridade (verde = very high, ciano = high, amarelo = conditional, cinza = common, laranja = ambíguo, roxo = desconhecido), **sempre acompanhadas de texto e ícones** (★★ ★ ◆ · ⚠ ?).

Depois que o RS some da tela, o último resultado fica visível por `holdSeconds` (4 s), marcado como "last seen".

### Tipos de sinal e contexto de scan

O RS nem sempre é um minério. O banco tem 5 classes:

| Classe | RS base | O overlay mostra |
|---|---|---|
| **Minério** (26) | 3170 Quantainium … 4300 Ice | `GOLD · 3 ROCKS · ★★ VERY HIGH PRIORITY` |
| **Tipo de asteroide** (espaço) | I 4000 · C 4700 · S 4720 · P 4750 · M 4850 · Q 4870 · E 4900 | `Q-TYPE ASTEROID · ◌ COMPOSITION UNKNOWN · Approach and scan` |
| **Depósito FPS** (superfície) | 3000 | `FPS DEPOSIT · ◌ MINERAL NOT IDENTIFIED BY RS` + minerais possíveis |
| **Depósito ROC** (superfície) | 4000 | `ROC DEPOSIT · …` |
| **Salvage** | 2000 | `SALVAGE PANEL · ✕ NOT MINING` (esmaecido) |

Todos multiplicam (N × base). **Carinite (3000)** fica no banco como `experimental`: nunca é usada para identificar nada, pois 3000 é o código genérico de FPS.

**Contexto de scan**: `Auto` (padrão) / `Space` / `Surface`. Ele elimina o impossível: no espaço não há depósitos FPS/ROC, e na superfície não há tipos de asteroide. Exemplo: `4000` é I-Type no espaço e ROC no solo.

Em **Auto o app detecta sozinho** onde você está, usando só os códigos que aparecem na tela. Um asteroide C/S/P/M/Q/E-Type (4700–4900) revela **espaço**; um depósito FPS (3000) revela **solo**. Só ensina um código estável, exato e lido sem correção de OCR. Minérios e salvage não ensinam nada, porque aparecem nos dois lugares. O contexto aprendido vale por `contextMemoryMinutes` (10), é sobrescrito por um sinal contrário mais novo e aparece no canto do overlay como `SPACE · auto`. Até o primeiro sinal revelador, valores como `4000` aparecem como **POSSIBLE**. `Ctrl+Shift+X` força Space/Surface manualmente, se quiser.

**Clusters mistos** (`matcher.mixedClusters`): o scanner soma sinais diferentes, ex. `31185 = Stileron + 7 ROC`. O resolver testa `A×n + B×m` com três salvaguardas:
- **priors**: um cluster homogêneo de minério ganha de uma soma mista igualmente exata (`7170 = Gold×2` vence `Quantainium + ROC`, que aparece como alternativa de ~7%);
- **soma exata**: mistos exigem erro 0 (`mixedMaxAbsoluteError`), porque com ~36 sinais quase todo número fica a ±5 de alguma soma;
- **modo**: `CrossKind` (padrão: minério + não-minério), `All` (inclui minério + minério, que deixa muita leitura ambígua) ou `Off`.

Priors ajustáveis em `config/settings.json → matcher`: `orePrior` 1.0, `asteroidTypePrior` 0.8, `rocDepositPrior` 0.8, `fpsDepositPrior` 0.6, `salvagePrior` 0.15, `mixedPrior` 0.1.

Importante para o OCR: como somas mistas "explicam" quase qualquer número, uma leitura errada (`15,420` → `16,420`) poderia ganhar explicação. Por isso o pipeline só para cedo com uma explicação **simples** (cluster homogêneo, leitura limpa); explicações mistas competem com as leituras das outras variantes.

**Localização (futuro):** cada sinal já tem um campo `systems` (Stanton/Pyro/Nyx) reservado. Quando houver dados por local, o resolver poderá eliminar depósitos impossíveis, como faz o Bedrock.

### Como a identificação funciona

1. Captura **só a ROI do RS** (BitBlt de poucos KB) a cada `ocrIntervalMs` (padrão 200 ms). Se os pixels não mudaram, o OCR é pulado.
2. Pré-processa (upscale, max-channel grayscale, inversão para texto escuro, margem branca).
3. OCR → extrai números → normaliza letras confundíveis de forma conservadora (só em tokens majoritariamente numéricos: "RS" e "MASS" nunca viram números).
4. Para cada material habilitado: `n = round(RS / baseRS)`, `erro = |RS − baseRS×n|`; candidato se `erro ≤ matcher.maxAbsoluteError` (padrão 6 — os minérios estão a 15 de distância).
5. Confiança = gaussiana do erro × participação entre candidatos. Leve preferência por clusters menores. Leituras que precisaram de substituição OCR nunca são "Exact" e perdem 15% de confiança por letra.
6. **Votação temporal**: um valor só vai para o overlay quando vence `voteMinAgreement` (3) dos últimos `voteWindow` (5) frames.

---

## Editar `materials.json`

Fica em `data/<versão>/materials.json` (ao lado do executável; no repositório em `data/default/`). Comentários `//` são permitidos.

```jsonc
{ "name": "Gold", "baseRS": 3585, "priority": "VeryHigh", "category": "Ore",
  "notes": "", "enabled": true, "notify": true, "aliases": [] }
```

- `priority`: `VeryHigh` | `High` | `Conditional` | `Common`
- `enabled: false` remove o material do reconhecimento
- `notify: true` toca o som (se *Sound alerts* estiver ligado em Settings)
- `aliases`: nomes alternativos (ex.: `Aluminium`) — usados pelo parser de composição futuro

Depois de editar, clique **Apply & save** em Settings (recarrega o DB). O app valida duplicatas e avisa.

## Databases por versão do jogo

```
data/
  default/materials.json
  4.10/materials.json
  4.10.1/materials.json
```

Copie a pasta `default` para uma nova versão, ajuste os valores e escolha em Settings → *Game version (DB)*. Se a pasta da versão não existir, o app usa `default`.

---

## Debug Mode

Settings → marque **Debug mode** → *Apply & save*, e abra **Ctrl+Shift+D**. A janela mostra:

- ROI capturada e imagem pré-processada enviada ao OCR
- texto bruto do OCR por variante (Grayscale/Binarized) com tempo
- números normalizados (e quantas substituições)
- RS detectado, buffer de votos, RS estável
- todos os candidatos com erro e confiança
- tempo de captura/processamento e taxa de OCR

*Freeze* congela a tela de debug para ler com calma. *Save unknown signature* grava a ROI.

## Assinaturas desconhecidas

**Ctrl+Shift+U** salva em `unknown_signatures/`: PNG da ROI + uma linha em `unknown_signatures.jsonl` (timestamp, RS, texto OCR, posição na tela, versão do DB). Útil para descobrir novos RS depois de patches.

## Testes com screenshots (sem o jogo)

- **No app:** Settings → *Analyze screenshot…* (usa sua calibração para recortar a ROI).
- **CLI:**

```bash
dotnet run --project src/MiningAssistant.Cli -- match 3585 10755 21505
dotnet run --project src/MiningAssistant.Cli -- analyze test_images/synthetic/gold-x3__rs10755.png
dotnet run --project src/MiningAssistant.Cli -- regress test_images -v
dotnet run --project src/MiningAssistant.Cli -- synth
dotnet run --project src/MiningAssistant.Cli -- live 20 --dump .scratch/live
```

`live` captura a ROI do RS direto do monitor do jogo (mesmo caminho do overlay) e mostra a leitura quadro a quadro; com `--dump` salva os recortes em qualidade nativa — ótimos para novos testes.

Screenshots nativos do próprio jogo (tecla de screenshot do SC → `StarCitizen\LIVE\screenshots`, 3440×1440) funcionam muito bem; prints redimensionados/comprimidos (chat, Discord) **não** servem — o número fica com 3–6 px e nenhum OCR lê.

Coloque screenshots reais em `test_images/real/` com o RS esperado no nome (`qualquer_coisa__rs3585.png`) e copie seu `config/profiles/default.roi.json` para lá como `profile.roi.json`. Veja [test_images/README.md](test_images/README.md).

---

## Arquitetura

```
src/
  MiningAssistant.Core     net8.0, sem UI/Windows — 100% testável
    Materials/             Material, MaterialDatabase, loader por versão
    Matching/              SignatureMatcher (múltiplos, confiança, ambiguidade)
    Ocr/                   OcrNumberNormalizer, SignatureExtractor
    Temporal/              TemporalVoter, ScanSession (votação + hold)
    Roi/                   RoiProfile, NormalizedRect
    Settings/              AppSettings, AppPaths
    Presentation/          OverlayPresenter (resultado → texto do overlay)
  MiningAssistant.Vision   captura GDI, pré-processamento, Windows OCR, ScanPipeline, LiveScanner
  MiningAssistant.App      WPF: overlay, calibração, settings, debug, hotkeys, tray
  MiningAssistant.Cli      match / analyze / regress / synth / ocr-langs
tests/MiningAssistant.Tests  xUnit
data/default/materials.json
test_images/
```

Interfaces `IScreenCapture` e `IOcrEngine` permitem trocar a captura (ex.: Windows Graphics Capture) ou o OCR (ex.: Tesseract) sem mexer no resto.

Arquivos gerados ao lado do executável: `config/settings.json`, `config/profiles/*.roi.json`, `config/app.log`, `unknown_signatures/`.

---

## Segurança e anti-cheat (Easy Anti-Cheat)

O Star Citizen usa Easy Anti-Cheat. O app foi desenhado para ficar **inteiramente fora do jogo**:

| O app faz | O app **não** faz |
|---|---|
| lê a imagem já composta da área de trabalho (`BitBlt`/`CopyFromScreen`, igual a print, OBS, Discord) — só um retângulo pequeno | abrir o processo do jogo (`OpenProcess`), ler/escrever memória |
| desenha **suas próprias** janelas (overlay transparente, click-through) | injetar DLL, hook de DirectX/Vulkan/WinAPI, overlay injetado |
| registra atalhos globais com `RegisterHotKey` (API oficial do Windows, não é hook) | enviar teclas/mouse ao jogo, macros, automação |
| lê/grava apenas arquivos na própria pasta | tocar arquivos do jogo; acessar rede |

Isso é **verificado automaticamente**: `tests/MiningAssistant.Tests/AntiCheatSafetyTests.cs` falha o build se qualquer código usar APIs de processo/memória/injeção/entrada sintética/rede ou importar qualquer função nativa fora de uma lista de 9 funções de `user32.dll`.

**Windows SmartScreen em outro PC:** o `.exe` não tem assinatura digital paga, então, ao abrir pela primeira vez um `.zip` baixado da internet, o Windows pode mostrar "o Windows protegeu o computador". Clique em *Mais informações → Executar assim mesmo*, ou, antes de abrir, clique com o botão direito no `.zip` → *Propriedades* → marque *Desbloquear*. Copiar a pasta por pendrive ou pela rede local não gera esse aviso. Uma solução definitiva seria um certificado de assinatura de código.
**Honestidade sobre o risco:** a detecção técnica do EAC mira injeção, leitura de memória e manipulação de arquivos — nada disso existe aqui. Mas ninguém fora da CIG pode garantir 100% como a política será aplicada. O EULA proíbe software de terceiros *"designed to modify the Game experience and/or give you an advantage"* e programas de macro/automação. Este app não modifica o jogo nem automatiza nada; ele só interpreta um número que já está na sua tela (o mesmo que consultar uma tabela de RS). Se quiser certeza absoluta, pergunte ao suporte da CIG antes de usar.

## Limitações conhecidas

- **Fullscreen exclusivo não é suportado** — use Borderless/Fullscreen Windowed (a captura GDI lê a área de trabalho composta).
- A precisão depende da calibração: ROI muito grande com outros números pode confundir (o app prioriza números com rótulo "RS" e que batem com minérios, mas não é mágica). Use o Debug Mode.
- O modo "imagem inteira" (screenshot sem perfil) é mais fraco que o modo ROI, porque o texto fica pequeno demais para o OCR sem ampliação.
- Os valores de RS mudam entre patches: mantenha `materials.json` atualizado; use *Save Unknown Signature*.
- Alguns múltiplos coincidem (ex.: 6×Gold = 21510 e 5×Ice = 21500; 5×Savrilium = 4×ROC = 16000; 3×ROC = 4×FPS = 12000). Nesses casos o app mostra **POSSIBLE** com as opções em vez de chutar; definir o contexto de scan resolve boa parte.
- Os códigos de tipo de asteroide, depósitos e salvage vêm de pesquisa em bases da comunidade (Prospector Index/UEX, STARVEIN, SCMINER, SC Wiki API) para 4.10/4.10.1, e ainda não foram confirmados por captura própria deste app. Use *Save Unknown Signature* para validar.
- O overlay é excluído das capturas de tela por padrão (`overlay.hideFromCapture`), então não aparece em OBS/prints. Se desligar para streaming, mantenha-o longe da ROI do RS.
- O modo *Detailed Mining* e o analisador de dificuldade da Prospector ainda não foram implementados.

## Próximos passos

1. Scan detalhado: OCR de Mass / Resistance / Instability / Composition / Quality nas regiões já calibráveis.
2. `loadouts.json` + analisador de dificuldade (EASY … LIKELY TOO LARGE — sempre marcado como ESTIMATE).
3. Composição com destaque pelo minério de maior prioridade.
4. Histórico e exportação CSV.
5. Auto-calibração por detecção de elementos do HUD.

*Projeto de comunidade, sem afiliação com a Cloud Imperium Games. Star Citizen® é marca da CIG.*
