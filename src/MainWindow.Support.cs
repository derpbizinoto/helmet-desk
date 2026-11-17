using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using HelmetDesk.Core;

namespace HelmetDesk
{
    /// <summary>Settings → Dados (export / import backup) and Suporte (bug report). The work itself lives in HelmetDesk.Core.</summary>
    public partial class MainWindow
    {
        private const string ZipFilter = "Backup do Helmet Desk (*.zip)|*.zip";
        private bool _backupBusy;

        internal static BackupService CreateBackupService() => new(new BackupOptions
        {
            DataDir = AppPaths.DataDir,
            SafetyBackupDir = AppPaths.SafetyBackupDir,
            DataFiles = new[] { "data.json" },
            DataFolders = new[] { "attachments" }, // note attachments (pictures, files): restoring a backup brings them back
            AppVersion = App.AppVersion,
            CurrentSchema = Store.CurrentSchema,
            Migrate = Store.Migrate,
        }, Store.Gate);

        internal static BugReportService ReportService { get; } = new(new BugReportOptions
        {
            AppVersion = App.AppVersion,
            LogDir = AppPaths.LogDir,
            OutputDir = AppPaths.ReportsDir,
            UserDataFiles = new[] { AppPaths.DataFile },
            // no Windows user name in the report
            Redactions = new System.Collections.Generic.Dictionary<string, string>
            {
                [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)] = "%USERPROFILE%",
            },
        });

        private void SetBackupBusy(bool busy, string text = null, int percent = 0)
        {
            _backupBusy = busy;
            ExportBtn.IsEnabled = ImportBtn.IsEnabled = !busy;
            BackupStatus.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            if (text != null) BackupStatusText.Text = text;
            BackupBar.Width = 320 * Math.Clamp(percent, 0, 100) / 100.0;
        }

        private IProgress<OperationProgress> BackupProgress() =>
            new Progress<OperationProgress>(p => SetBackupBusy(true, p.Message, p.Percent)); // reported on the UI thread

        // ---------------- export ----------------

        private async void ExportBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_backupBusy) return;
            var svc = CreateBackupService();
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Exportar backup",
                Filter = ZipFilter,
                DefaultExt = ".zip",
                AddExtension = true,
                FileName = svc.SuggestedFileName(DateTimeOffset.Now),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog(this) != true) return;
            await ExportTo(svc, dlg.FileName);
        }

        internal async Task<bool> ExportTo(BackupService svc, string path)
        {
            SetBackupBusy(true, "Preparando…", 0);
            try
            {
                Store.Save(); // write pending changes first
                await svc.ExportAsync(path, BackupProgress());
                SetBackupBusy(false, $"Backup exportado: {Path.GetFileName(path)}", 100);
                ShowSnack("Backup exportado", "Mostrar", () => RevealFile(path));
                Log.Info($"Backup exportado para {path}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Falha ao exportar backup", ex);
                SetBackupBusy(false, null);
                OpenDialog("Não foi possível exportar", ex is BackupException ? ex.Message : "Erro ao gravar o arquivo: " + ex.Message,
                    "OK", null, DialogKind.Error, false, null);
                return false;
            }
        }

        // ---------------- import ----------------

        private async void ImportBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_backupBusy) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Importar backup",
                Filter = ZipFilter,
                CheckFileExists = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog(this) != true) return;
            await ImportFrom(CreateBackupService(), dlg.FileName, confirm: true);
        }

        /// <param name="confirm">false only in automated tests.</param>
        internal async Task<bool> ImportFrom(BackupService svc, string path, bool confirm, bool restart = true)
        {
            // 1. check it without touching anything
            BackupManifest m;
            SetBackupBusy(true, "Verificando o backup…", 0);
            try { m = await svc.InspectAsync(path, BackupProgress()); }
            catch (Exception ex)
            {
                Log.Warn($"Backup recusado: {path}", ex);
                SetBackupBusy(false, null);
                OpenDialog("Backup não pode ser importado", ex is BackupException ? ex.Message : "Não foi possível ler o arquivo: " + ex.Message,
                    "OK", null, DialogKind.Error, false, null);
                return false;
            }
            SetBackupBusy(false, null);

            // 2. ask
            if (confirm)
            {
                var when = m.ExportedAtUtc.ToLocalTime();
                var tcs = new TaskCompletionSource<bool>();
                OpenDialog("Substituir os dados atuais?",
                    $"Backup de {when:dd/MM/yyyy 'às' HH:mm} (Helmet Desk {m.AppVersion}).\n\n" +
                    "Todas as notas, tarefas, anexos, etiquetas e configurações atuais serão substituídas pelas do backup. " +
                    "Uma cópia de segurança dos dados atuais é guardada antes. O Helmet Desk vai reiniciar em seguida.",
                    "Substituir e reiniciar", "Cancelar", DialogKind.Danger, false, ok => tcs.TrySetResult(ok));
                if (!await tcs.Task) return false;
            }

            // 3. replace (nothing is saved over the new data from here on)
            Store.Save();
            Store.Frozen = true;
            SetBackupBusy(true, "Importando…", 0);
            try
            {
                var safety = await svc.ImportAsync(path, BackupProgress());
                Log.Info($"Backup importado de {path}; cópia de segurança em {safety}");
                SetBackupBusy(false, "Backup importado. Reiniciando…", 100);
            }
            catch (Exception ex)
            {
                Store.Frozen = false;
                Log.Error("Falha ao importar backup", ex);
                SetBackupBusy(false, null);
                OpenDialog("A importação falhou", (ex is BackupException ? ex.Message : ex.Message) + "\n\nSeus dados atuais não foram alterados.",
                    "OK", null, DialogKind.Error, false, null);
                return false;
            }
            if (restart) App.Current.Restart();
            return true;
        }

        // ---------------- support ----------------

        private void ReportBug_Click(object sender, RoutedEventArgs e) => OpenBugReport();

        public void OpenBugReport(string exception = null)
        {
            if (!IsVisible) App.Current.ShowMain();
            var w = new BugReportWindow(ReportService, exception) { Owner = this };
            w.ShowDialog();
        }

        /// <summary>Next start after a crash: offer to send it.</summary>
        public void OfferCrashReport(string crash)
        {
            ShowSnack("O Helmet Desk fechou por causa de um erro da última vez.", "Reportar", () => OpenBugReport(crash));
        }

        /// <summary>
        /// What "Limpar cache" deletes: things the app rebuilds or no longer needs. Never data.json, attachments,
        /// the pre-import safety backups or the logs (the bug report reads them).
        /// </summary>
        private static CacheTarget[] CacheTargets()
        {
            var tmp = Path.GetTempPath();
            return new[]
            {
                new CacheTarget(LinkPreview.CacheDir),                              // link titles + site icons (fetched again)
                new CacheTarget(AppPaths.ReportsDir, "*.zip"), // bug reports already generated
                new CacheTarget(tmp, "hd-selftest-*"),                              // self-test leftovers
                new CacheTarget(tmp, "hd-zip-check"),
                new CacheTarget(Path.GetDirectoryName(AppPaths.DataDir.TrimEnd('\\', '/')),  // half-finished import, if one was cut short
                    Path.GetFileName(AppPaths.DataDir.TrimEnd('\\', '/')) + ".import-tmp"),
            };
        }

        private async void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            if (_backupBusy) return; // an import uses the .import-tmp folder
            ClearCacheBtn.IsEnabled = false;
            try
            {
                var targets = CacheTargets();
                long size = await Task.Run(() => CacheService.Measure(targets));
                if (size == 0) { ShowSnack("O cache já está vazio", null, null); return; }

                var answer = new TaskCompletionSource<bool>();
                OpenDialog("Limpar cache",
                    $"Isso libera {CacheService.FormatSize(size)} (prévias de links e relatórios de erro antigos). " +
                    "Suas notas, tarefas, finanças, anexos e backups não são afetados.",
                    "Limpar", "Cancelar", DialogKind.Info, false, r => answer.TrySetResult(r));
                if (!await answer.Task) return;

                LinkPreview.ForgetAll();
                var res = await Task.Run(() => CacheService.Clear(targets));
                Log.Info($"cache: {res.Deleted} arquivo(s) apagado(s), {res.FreedBytes} bytes, {res.Skipped} em uso");
                ShowSnack(res.Skipped > 0
                    ? $"{CacheService.FormatSize(res.FreedBytes)} liberados ({res.Skipped} arquivo(s) em uso ficaram)"
                    : $"Cache limpo: {CacheService.FormatSize(res.FreedBytes)} liberados", null, null);
            }
            catch (Exception ex) { Log.Warn("cache: falha ao limpar", ex); ShowSnack("Não foi possível limpar o cache", null, null); }
            finally { ClearCacheBtn.IsEnabled = true; }
        }

        private void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDir}\"") { UseShellExecute = true });
        }

        /// <summary>Opens Explorer with the file selected.</summary>
        public static void RevealFile(string path)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("Não foi possível abrir o Explorer", ex); }
        }
    }
}
