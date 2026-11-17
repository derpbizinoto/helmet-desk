using System;
using System.Windows;

namespace HelmetDesk
{
    /// <summary>Settings → Atualizações, and the "update ready" notice (the usual snackbar). The work is in <see cref="UpdateService"/>.</summary>
    public partial class MainWindow
    {
        /// <summary>The notice stays up longer than a regular snack; it can also be acted on later from Settings.</summary>
        private const int UpdateSnackSeconds = 20;
        private string _pendingUpdateNotice;

        private void HookUpdates()
        {
            var up = UpdateService.Instance;
            up.Changed += SyncUpdateCard;
            up.UpdateReady += OnUpdateReady;
            // window hidden in the tray when the download finished: show the notice the next time it opens
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible && _pendingUpdateNotice != null) Dispatcher.BeginInvoke(new Action(ShowUpdateNotice));
            };
            SyncUpdateCard();
        }

        private void SyncUpdateCard()
        {
            var up = UpdateService.Instance;
            UpdateVersionText.Text = $"Versão atual: {up.CurrentVersion}";
            var status = up.State == UpdateState.Idle ? "" : up.StatusText;
            UpdateStatusText.Text = status;
            UpdateStatusText.Visibility = status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            CheckUpdatesBtn.IsEnabled = up.State is not (UpdateState.Checking or UpdateState.Downloading);
            ApplyUpdateBtn.Visibility = up.State == UpdateState.Ready ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnUpdateReady(string version)
        {
            _pendingUpdateNotice = version;
            if (IsVisible) ShowUpdateNotice();
            else App.Current.Notify($"A versão {version} foi baixada e será instalada quando você fechar o Helmet Desk.", "Atualização pronta");
        }

        private void ShowUpdateNotice()
        {
            var v = _pendingUpdateNotice;
            if (v == null) return;
            _pendingUpdateNotice = null;
            ShowSnack($"Nova versão {v} pronta", "Reiniciar e atualizar", UpdateService.Instance.ApplyNow, UpdateSnackSeconds);
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            try { await UpdateService.Instance.CheckNowAsync(); }
            catch (Exception ex) { Log.Warn("atualização: verificação manual falhou", ex); }
        }

        private void ApplyUpdate_Click(object sender, RoutedEventArgs e) => UpdateService.Instance.ApplyNow();
    }
}
