# Como voltar para a UI antiga ("Violet Terminal")

Esta pasta guarda uma cópia da interface do Helmet Desk **antes** do redesign Frutiger Aero
(13/11/2026). Ela não é compilada: o projeto fica em `src/`, e esta pasta fica fora dele.

O que tem aqui (`src/`): todos os `.xaml` e seu code-behind, `Theme.xaml`, `Controls.cs`, `Motion.cs`,
`LottieView.cs`, `MainWindow.Support.cs`, `Models.cs` (paleta de etiquetas), o `.csproj` e a pasta
`Assets/` inteira (logo, ícone, fontes Geo/Roboto, animação).

## A) Pelo git

O estado antigo está marcado com a tag `ui-legacy-backup`; o redesign é feito na branch `ui-frutiger-aero`.

- Só olhar/rodar a versão antiga:
  ```
  git checkout ui-legacy-backup
  dotnet publish src\HelmetDesk.csproj -c Release -r win-x64 --self-contained false -o app
  ```
  (depois `git checkout ui-frutiger-aero` para voltar)
- Desistir do redesign de vez: continue na branch principal sem fazer merge de `ui-frutiger-aero`,
  ou, se o merge já foi feito, `git revert -m 1 <commit-do-merge>`.

## B) Pela troca de tema (sem git)

Depois do redesign, o app terá os dois visuais:

- `src/Themes/Legacy.xaml` — o visual antigo;
- `src/Themes/FrutigerAero.xaml` — o visual novo.

Em **Configurações > Geral > Tema**, escolha **Clássico** para voltar ao visual antigo.

## C) Na mão (último recurso)

Copie os arquivos de `_backup/ui-legacy/src/` por cima dos de `src/` e recompile. Atenção: isso também
volta qualquer correção feita nesses arquivos depois desta data.
