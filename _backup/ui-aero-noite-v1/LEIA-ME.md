# Backup: tema Frutiger Aero escuro, antes do refinamento (16/11/2026)

Cópia de `src/Themes/` (FrutigerAero.xaml, Legacy.xaml, Shared.xaml, ThemeManager.cs) e de todos os `src/*.xaml`
como estavam no commit `424f4e6`. Esta pasta não é compilada (o .csproj exclui `_backup/**`).

## Voltar pelo git
O mesmo estado está na tag `ui-aero-noite-v1`:

    git checkout ui-aero-noite-v1 -- src/Themes src/App.xaml src/BugReportWindow.xaml src/FinanceView.xaml src/MainWindow.xaml src/StickyWindow.xaml src/TasksWidget.xaml

## Voltar na mão
Copie `src/Themes/*` e `src/*.xaml` desta pasta por cima dos de `src/` e recompile:

    dotnet publish src\HelmetDesk.csproj -c Release -r win-x64 --self-contained false -o app

O refinamento só mexe em XAML (tema e telas); nenhum .cs de lógica muda.
