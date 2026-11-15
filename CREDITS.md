# Créditos

## Animações

As animações do Helmet Desk vêm do [LottieFiles](https://lottiefiles.com) e são usadas sob a
[Lottie Simple License](https://lottiefiles.com/page/license). Só animações gratuitas, nenhuma premium.

Os arquivos ficam em `src/Assets/Animations/`.

| Arquivo | Uso no app | Link original | Autor | Licença |
|---|---|---|---|---|
| `tarefa-concluida.lottie` (1,8 KB) | Feedback ao concluir uma tarefa | [checkmark](https://lottiefiles.com/free-animation/checkmark-3yTUIXzeNb) | Jenny Seong | Lottie Simple License — **modificada**: cores trocadas para a paleta do app (anel `#8950D9`, check `#F5F1FA`) |

## Sons

Efeitos do pacote [Interface Sounds](https://kenney.nl/assets/interface-sounds) da Kenney, domínio público
([CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/)). Convertidos de OGG para WAV PCM 16-bit 44,1 kHz
(sem outra alteração) e embutidos no executável. Ficam em `src/Assets/Sounds/`.

| Arquivo final | Arquivo original | Uso no app | Origem / autor | Licença |
|---|---|---|---|---|
| `tarefa-concluida.wav` | `confirmation_001.ogg` | Ao concluir uma tarefa | Kenney – Interface Sounds, kenney.nl | CC0 1.0 |
| `tudo-feito.wav` | `confirmation_002.ogg` | Ao concluir a última tarefa pendente | Kenney – Interface Sounds, kenney.nl | CC0 1.0 |

## Bibliotecas

| Biblioteca | Uso | Licença |
|---|---|---|
| [SkiaSharp](https://github.com/mono/SkiaSharp) / SkiaSharp.Skottie | Reprodução das animações Lottie | MIT |
| [NAudio](https://github.com/naudio/NAudio) (NAudio.Core, NAudio.WinMM) | Reprodução dos efeitos sonoros com volume | MIT |
| [Serilog](https://serilog.net) / Serilog.Sinks.File | Log técnico com rolagem diária | Apache-2.0 |
| [xUnit](https://xunit.net) (só nos testes) | Testes dos serviços de backup e report | Apache-2.0 |

## Identidade visual

Ícones no estilo [Lucide](https://lucide.dev) (ISC), redesenhados como caminhos vetoriais no tema.

## Fontes

Baixadas do [Google Fonts](https://fonts.google.com) por `tools/baixar-fontes.ps1` e embutidas no executável
(`src/Assets/Fonts/`, junto com o texto de cada licença). O app não acessa a internet para usá-las.

| Fonte | Uso no app | Link | Autor | Licença |
|---|---|---|---|---|
| Geo (Regular, Italic) | Nome "Helmet Desk" na barra lateral | [fonts.google.com/specimen/Geo](https://fonts.google.com/specimen/Geo) | Ben Weiner | SIL Open Font License 1.1 |
| Roboto (Light, Regular, Medium, SemiBold, Bold, ExtraBold + itálicos) | Todo o resto da interface | [fonts.google.com/specimen/Roboto](https://fonts.google.com/specimen/Roboto) | The Roboto Project Authors | SIL Open Font License 1.1 |
