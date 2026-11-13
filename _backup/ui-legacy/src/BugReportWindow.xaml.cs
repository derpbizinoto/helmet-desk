using System;
using System.Windows;
using System.Windows.Input;
using HelmetDesk.Core;

namespace HelmetDesk
{
    /// <summary>"Reportar problema": form → preview → save the package. All the work is in <see cref="IBugReportService"/>.</summary>
    public partial class BugReportWindow : Window
    {
        private readonly IBugReportService _svc;
        private readonly string _exception;
        private BugReportPackage _package;
        private bool _busy;

        public BugReportWindow(IBugReportService svc, string exception = null)
        {
            InitializeComponent();
            _svc = svc;
            _exception = exception;
            if (!string.IsNullOrWhiteSpace(exception))
            {
                CrashBox.Visibility = Visibility.Visible;
                CrashText.Text = exception.Trim();
                TitleBox.Text = "O Helmet Desk apresentou um erro";
            }
            SourceInitialized += (s, e) => Native.Round(this);
            Loaded += (s, e) => { TitleBox.Focus(); TitleBox.SelectAll(); };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && !_busy) { Back_Click(null, null); e.Handled = true; } };
        }

        private bool OnPreview => PreviewPage.Visibility == Visibility.Visible;

        private BugReportInput Input() => new()
        {
            Title = TitleBox.Text,
            Description = DescBox.Text,
            Email = EmailBox.Text,
            IncludeLogs = LogsCheck.IsChecked == true,
            IncludeUserData = DataCheck.IsChecked == true,
            Exception = _exception,
        };

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (!OnPreview)
            {
                var input = Input();
                if (_svc.Validate(input) is string problem) { Status.Text = problem; Status.Foreground = Ui.Res("Danger"); return; }
                _package = _svc.Build(input);
                PreviewBox.Text = _package.PreviewText;
                ShowPage(preview: true);
                return;
            }

            // confirmed: save (and, in the future, send)
            SetBusy(true, "Gerando o relatório…");
            var progress = new Progress<OperationProgress>(p => Status.Text = p.Message);
            BugReportResult r;
            try { r = await _svc.SubmitAsync(_package, progress); }
            catch (Exception ex) { r = new BugReportResult { Outcome = ReportOutcome.Failed, Message = "Erro inesperado: " + ex.Message }; }
            SetBusy(false, r.Message);

            switch (r.Outcome)
            {
                case ReportOutcome.Sent:
                case ReportOutcome.SavedOffline:
                    Log.Info($"Relatório de problema gerado: {r.ZipPath ?? "(enviado)"}");
                    Status.Foreground = Ui.Res("Success");
                    if (r.ZipPath != null) MainWindow.RevealFile(r.ZipPath);
                    NextBtn.Content = "Concluído";
                    NextBtn.Click -= Next_Click;
                    NextBtn.Click += (s, ev) => Close();
                    BackBtn.Visibility = Visibility.Collapsed;
                    break;
                default:
                    Status.Foreground = Ui.Res(r.Outcome == ReportOutcome.RateLimited ? "Warning" : "Danger");
                    break;
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (OnPreview && NextBtn.Content as string != "Concluído") { ShowPage(preview: false); return; }
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { if (!_busy) Close(); }

        private void ShowPage(bool preview)
        {
            FormPage.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
            PreviewPage.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
            Heading.Text = preview ? "Revisar relatório" : "Reportar problema";
            NextBtn.Content = preview ? "Gerar relatório" : "Revisar";
            BackBtn.Content = preview ? "Voltar" : "Cancelar";
            Status.Text = "";
            Status.Foreground = Ui.Res("Text2");
            Motion.Enter(preview ? PreviewPage : FormPage, dx: preview ? Motion.ShiftSmall : -Motion.ShiftSmall);
        }

        private void SetBusy(bool busy, string text)
        {
            _busy = busy;
            NextBtn.IsEnabled = BackBtn.IsEnabled = !busy;
            Status.Text = text ?? "";
            Status.Foreground = Ui.Res("Text2");
            Cursor = busy ? Cursors.AppStarting : null;
        }
    }
}
