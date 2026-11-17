using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace HelmetDesk
{
    public enum UpdateState { NotInstalled, Idle, Checking, UpToDate, Downloading, Ready, Failed }

    /// <summary>
    /// Automatic updates through Velopack, from the public GitHub releases of the project.
    /// - Not installed (running from Visual Studio / bin): does nothing, raises nothing.
    /// - Checks in the background shortly after start; downloads in the background; then offers "Reiniciar e atualizar".
    ///   If the user ignores it, the update is applied after the app closes (<see cref="ApplyOnExit"/>).
    /// - Any network or update failure is only logged; it never reaches the user as an error and never takes the app down.
    /// No token here, ever: public releases need no authentication.
    /// </summary>
    public sealed class UpdateService
    {
        /// <summary>Where releases are published. Change here if the repository moves.</summary>
        public const string RepoUrl = "https://github.com/derpbizinoto/helmet-desk";

        /// <summary>How long after start the first check runs, so it never competes with opening the app.</summary>
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(8);

        public static UpdateService Instance { get; } = new();

        private UpdateManager _mgr;
        private VelopackAsset _ready;
        private bool _busy, _applying;

        /// <summary>Raised on the UI thread whenever <see cref="State"/> or <see cref="StatusText"/> changes.</summary>
        public event Action Changed;
        /// <summary>Raised on the UI thread once an update is downloaded and can be applied (argument: new version).</summary>
        public event Action<string> UpdateReady;

        public UpdateState State { get; private set; } = UpdateState.Idle;
        public int Progress { get; private set; }
        public string ReadyVersion => _ready?.Version?.ToString();

        private UpdateService()
        {
            try
            {
                _mgr = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
                if (!_mgr.IsInstalled) State = UpdateState.NotInstalled;
            }
            catch (Exception ex)
            {
                _mgr = null;
                State = UpdateState.NotInstalled;
                Log.Warn("atualização: não foi possível iniciar o Velopack", ex);
            }
        }

        public bool IsInstalled => _mgr?.IsInstalled == true;

        /// <summary>Installed version (from the package), or the assembly version when running uninstalled.</summary>
        public string CurrentVersion => (IsInstalled ? _mgr.CurrentVersion?.ToString() : null) ?? App.AppVersion;

        public string StatusText => State switch
        {
            UpdateState.NotInstalled => T("As atualizações automáticas funcionam apenas na versão instalada."),
            UpdateState.Checking => T("Procurando atualizações…"),
            UpdateState.UpToDate => T("Você está usando a versão mais recente."),
            UpdateState.Downloading => TF("Baixando a versão {0}… {1}%", ReadyVersionOrPending, Progress),
            UpdateState.Ready => InstallMode.NeedsAdmin
                ? TF("A versão {0} está pronta. Clique em Reiniciar e atualizar para instalar (o Windows vai pedir permissão de administrador).", ReadyVersion)
                : TF("A versão {0} está pronta. Ela será instalada ao reiniciar ou quando você fechar o Helmet Desk.", ReadyVersion),
            UpdateState.Failed => T("Não foi possível verificar agora. Tente de novo mais tarde."),
            _ => "",
        };

        private string _pendingVersion;
        private string ReadyVersionOrPending => ReadyVersion ?? _pendingVersion ?? T("nova");

        /// <summary>Called once at startup: waits a bit, then checks and downloads without touching the UI thread.</summary>
        public void StartBackgroundCheck()
        {
            if (!IsInstalled) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(StartupDelay).ConfigureAwait(false);
                    await OnUi(() => CheckAsync(manual: false)).ConfigureAwait(false);
                }
                catch (Exception ex) { Log.Warn("atualização: checagem em segundo plano interrompida", ex); }
            });
        }

        /// <summary>Settings → "Verificar atualizações". Silent on failure (status text only).</summary>
        public Task CheckNowAsync() => CheckAsync(manual: true);

        private async Task CheckAsync(bool manual)
        {
            if (!IsInstalled) { if (manual) Set(UpdateState.NotInstalled); return; }
            if (_busy || _applying) return;
            if (_ready != null) { Set(UpdateState.Ready); return; }
            _busy = true;
            try
            {
                // downloaded in an earlier session and not applied yet (all-users install: never applied at startup)
                var pending = _mgr.UpdatePendingRestart;
                if (pending != null && (_mgr.CurrentVersion == null || pending.Version > _mgr.CurrentVersion))
                {
                    _ready = pending;
                    Log.Info($"atualização: versão {ReadyVersion} já baixada, aguardando instalação");
                    Set(UpdateState.Ready);
                    UpdateReady?.Invoke(ReadyVersion);
                    return;
                }

                Set(UpdateState.Checking);
                Log.Info($"atualização: verificando ({(manual ? "manual" : "automática")}, versão atual {CurrentVersion})");
                var info = await Task.Run(() => _mgr.CheckForUpdatesAsync());
                if (info == null)
                {
                    Log.Info("atualização: nenhuma versão nova");
                    Set(UpdateState.UpToDate);
                    return;
                }

                _pendingVersion = info.TargetFullRelease?.Version?.ToString();
                Log.Info($"atualização: versão {_pendingVersion} encontrada, baixando");
                Progress = 0;
                Set(UpdateState.Downloading);
                var dispatcher = Application.Current?.Dispatcher;
                await Task.Run(() => _mgr.DownloadUpdatesAsync(info, p =>
                {
                    dispatcher?.BeginInvoke(new Action(() => { Progress = p; Changed?.Invoke(); }));
                }));

                _ready = info.TargetFullRelease;
                Log.Info($"atualização: versão {ReadyVersion} baixada e pronta");
                Set(UpdateState.Ready);
                UpdateReady?.Invoke(ReadyVersion);
            }
            catch (Exception ex)
            {
                Log.Warn("atualização: falha ao verificar ou baixar", ex);
                Set(UpdateState.Failed);
            }
            finally { _busy = false; }
        }

        /// <summary>"Reiniciar e atualizar": saves, closes, Velopack applies the update and opens the new version.</summary>
        public void ApplyNow()
        {
            if (_ready == null || _applying || !IsInstalled) return;
            var target = _ready;
            Log.Info($"atualização: reiniciando para instalar {ReadyVersion}");
            App.Current.ExitApp(() =>
            {
                _applying = true;
                try { _mgr.WaitExitThenApplyUpdates(target, silent: false, restart: true); }
                catch (Exception ex) { Log.Error("atualização: não foi possível aplicar", ex); }
            });
        }

        /// <summary>
        /// On a normal exit: if an update is downloaded, apply it after the process ends (silently, no restart).
        /// Not when the install needs admin rights: that would put a UAC prompt on screen as the app closes.
        /// </summary>
        public void ApplyOnExit()
        {
            if (_ready == null || _applying || !IsInstalled) return;
            if (InstallMode.NeedsAdmin)
            {
                Log.Info($"atualização: {ReadyVersion} fica para quando o usuário clicar em Reiniciar e atualizar (instalação exige administrador)");
                return;
            }
            _applying = true;
            try
            {
                Log.Info($"atualização: {ReadyVersion} será instalada após fechar");
                _mgr.WaitExitThenApplyUpdates(_ready, silent: true, restart: false);
            }
            catch (Exception ex) { Log.Error("atualização: não foi possível agendar a instalação ao fechar", ex); }
        }

        private void Set(UpdateState s)
        {
            State = s;
            Changed?.Invoke();
        }

        private static Task OnUi(Func<Task> work)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null) return Task.CompletedTask;
            return d.InvokeAsync(work).Task.Unwrap();
        }
    }

    /// <summary>Kept apart from UpdateService so Program.Main can read it before Velopack runs, without creating the UpdateManager.</summary>
    public static class InstallMode
    {
        /// <summary>
        /// True when the install folder is not writable by the user (MSI "todos os usuários", in Program Files):
        /// applying an update needs UAC. Then the update is only applied when the user clicks "Reiniciar e atualizar",
        /// never on exit or at startup, so UAC never shows up by surprise. Computed once, also used by Program.Main.
        /// </summary>
        public static bool NeedsAdmin => _needsAdmin.Value;
        private static readonly Lazy<bool> _needsAdmin = new(ProbeNeedsAdmin);

        private static bool ProbeNeedsAdmin()
        {
            try
            {
                // installed layout: <root>\current\HelmetDesk.exe; Velopack writes packages\ and Update.exe in <root>
                var current = AppContext.BaseDirectory.TrimEnd('\\', '/');
                if (!string.Equals(Path.GetFileName(current), "current", StringComparison.OrdinalIgnoreCase)) return false;
                var root = Path.GetDirectoryName(current);
                var probe = Path.Combine(root, ".helmetdesk-write-test-" + Guid.NewGuid().ToString("N"));
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return false;
            }
            catch (UnauthorizedAccessException) { return true; }
            catch (IOException) { return true; }
            catch { return false; }
        }
    }
}