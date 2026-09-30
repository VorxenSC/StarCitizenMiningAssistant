# Star Citizen Mining Assistant

Overlay para Windows que **lê a tela** durante a mineração com Prospector/MOLE, reconhece o **RS (radar signature)** e mostra qual minério provavelmente é — incluindo **quantas rochas** há no cluster (RS = RS base × nº de rochas).

```
┌──────────────────────┐
│ ⛏ MINING ASSISTANT   │
│ GOLD                 │
│ RS 10,755            │
│ 3 ROCKS              │
│ ★★ VERY HIGH PRIORITY│
└──────────────────────┘
```

> **Copiloto visual, não um bot.** O app só observa a tela exatamente como você vê. Ele **não** injeta DLL, **não** lê memória, **não** modifica arquivos do jogo, **não** intercepta rede, **não** faz hook e **não** envia nenhuma tecla/mouse ao jogo. Todo processamento (captura + OCR) é local; nada é enviado para a internet.

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
- Windows 10 2004+ / Windows 11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (para rodar) ou .NET 8 SDK (para compilar)
- Pacote OCR de inglês do Windows (normalmente já instalado — veja abaixo)
- Star Citizen em **Borderless Windowed** ou **Fullscreen Windowed**

### Compilar

```bash
dotnet build -c Release
```

### Gerar uma pasta portátil

```bash
dotnet publish src/MiningAssistant.App -c Release -r win-x64 --self-contained false -o publish
```

Rode `publish\MiningAssistant.exe`. O app aparece na **bandeja do sistema** (ícone genérico de aplicativo perto do relógio, tooltip "Star Citizen Mining Assistant"): duplo clique abre Settings; botão direito mostra o menu.

### Testes

```bash
dotnet test
```

---

## Configurar o OCR

O app usa o OCR embutido do Windows (`Windows.Media.Ocr`) — rápido, local, sem downloads extras. Ele precisa do componente OCR de um idioma. Para verificar:

```bash
dotnet run --project src/MiningAssistant.Cli -- ocr-langs
```

Se `en-US` não aparecer, instale (PowerShell **como administrador**):

```powershell
Add-WindowsCapability -Online -Name "Language.OCR~~~en-US~0.0.1.0"
```

Se o idioma configurado não existir, o app usa automaticamente os idiomas do seu perfil (pt-BR também lê números). O idioma fica em `config/settings.json` → `ocr.language`.

Outras opções em `ocr`:
- `upscale` (padrão 3): amplia a ROI antes do OCR. Números do HUD são pequenos; 2–4 ajuda muito.
- `preprocess`: `Auto` (tenta tons de cinza e, se necessário, binarizado), `Grayscale` ou `Binarize`.
- `normalizer.maxSubstitutions` (padrão 2): quantas letras confundíveis (S→5, B→8, O→0, I/l→1, Z→2) podem ser corrigidas por número.

---

## Calibrar a ROI (obrigatório no primeiro uso)

1. Abra o Star Citizen e deixe um RS visível na tela (faça um scan).
2. Pressione **Ctrl+Shift+C** (ou Settings → *Calibrate ROI…*). A tela congela com um screenshot.
3. Selecione **RS Signature** e **arraste um retângulo** sobre a área onde o número do RS aparece. Deixe uma margem — não precisa ser justo; o app prefere números que batem com minérios conhecidos.
4. (Opcional) desenhe *Scan Details*, *Mass*, *Resistance*, *Instability*, *Composition* — já ficam salvos para o modo detalhado futuro.
5. **Enter** salva, **Esc** cancela. Use *Move toolbar ⇅* se a barra estiver em cima da área.

As regiões são salvas como **porcentagem do monitor** (`config/profiles/default.roi.json`), então o mesmo perfil se adapta aproximadamente a outras resoluções. Se você trocar a resolução/HUD, recalibre para melhor precisão.

**Monitor:** escolha o monitor do jogo em Settings → *Game monitor*. O app é *Per-Monitor DPI aware*: funciona com escala do Windows 100/125/150% e em ultrawide (testado em 3440×1440).

> Dica: se o número do RS aparecer em posições diferentes (por exemplo acompanhando o marcador da rocha), desenhe uma ROI maior ao redor do centro da mira. Custa alguns ms a mais de OCR mas continua leve.

---

## Usando

| Hotkey | Ação |
|---|---|
| **Ctrl+Shift+M** | mostrar/esconder overlay |
| **Ctrl+Shift+S** | quick scan (zera votos e escaneia agora) |
| **Ctrl+Shift+C** | calibração |
| **Ctrl+Shift+P** | pausar/retomar OCR |
| **Ctrl+Shift+U** | salvar assinatura desconhecida |
| **Ctrl+Shift+D** | janela de debug |

Hotkeys são editáveis em `config/settings.json` → `hotkeys` (formato `"Ctrl+Alt+F9"`). Se alguma estiver em uso por outro programa, isso é registrado em `config/app.log`.

### O que o overlay mostra

| Situação | Overlay |
|---|---|
| Nada detectado | pílula quase invisível "⛏ scanning" |
| RS exato | **GOLD** · RS 3,585 · 1 ROCK · ★★ VERY HIGH PRIORITY |
| RS próximo (ex.: 3584) | **GOLD** · … · Confidence 97% |
| Dois candidatos parecidos | **⚠ POSSIBLE:** GOLD ×6 · ICE ×5 com as porcentagens |
| RS desconhecido | **? UNKNOWN** · RS 5,000 · "Save with Ctrl+Shift+U" |
| Filtro de alvos ligado e achou | faixa **◎ TARGET FOUND** |

Cores por prioridade (verde = very high, ciano = high, amarelo = conditional, cinza = common, laranja = ambíguo, roxo = desconhecido) **sempre acompanhadas de texto e ícones** (★★ ★ ◆ · ⚠ ?).

Depois que o RS some da tela o último resultado fica visível por `holdSeconds` (4 s) marcado como "last seen".

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
```

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

## Limitações conhecidas

- **Fullscreen exclusivo não é suportado** — use Borderless/Fullscreen Windowed (a captura GDI lê a área de trabalho composta).
- A precisão depende da calibração: ROI muito grande com outros números pode confundir (o app prioriza números com rótulo "RS" e que batem com minérios, mas não é mágica). Use o Debug Mode.
- O modo "imagem inteira" (screenshot sem perfil) é mais fraco que o modo ROI, porque o texto fica pequeno demais para o OCR sem ampliação.
- Os valores de RS mudam entre patches: mantenha `materials.json` atualizado; use *Save Unknown Signature*.
- Alguns múltiplos coincidem quase exatamente (ex.: 6×Gold = 21510 e 5×Ice = 21500). Nesses casos o app mostra **POSSIBLE** com os dois em vez de chutar.
- O overlay é excluído das capturas de tela por padrão (`overlay.hideFromCapture`), então não aparece em OBS/prints. Se desligar para streaming, mantenha-o longe da ROI do RS.
- O modo *Detailed Mining* e o analisador de dificuldade da Prospector ainda não foram implementados.

## Próximos passos

1. Scan detalhado: OCR de Mass / Resistance / Instability / Composition / Quality nas regiões já calibráveis.
2. `loadouts.json` + analisador de dificuldade (EASY … LIKELY TOO LARGE — sempre marcado como ESTIMATE).
3. Composição com destaque pelo minério de maior prioridade.
4. Histórico e exportação CSV.
5. Auto-calibração por detecção de elementos do HUD.

*Projeto de comunidade, sem afiliação com a Cloud Imperium Games. Star Citizen® é marca da CIG.*
