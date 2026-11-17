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
        private bool _done; // the report was saved: "Concluído" closes the window

        private void ShowProblem(string text)
        {
            Status.Text = text;
            Status.Foreground = Ui.Res("Danger");
        }

        public BugReportWindow(IBugReportService svc, string exception = null)
        {
            InitializeComponent();
            _svc = svc;
            _exception = exception;
            if (!string.IsNullOrWhiteSpace(exception))
            {
                CrashBox.Visibility = Visibility.Visible;
                CrashText.Text = exception.Trim();
                TitleBox.Text = T("O Helmet Desk apresentou um erro");
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
                if (_svc.Validate(input) is string problem) { ShowProblem(problem); return; }
                try { _package = _svc.Build(input); }
                catch (Exception ex)
                {
                    Log.Error("Falha ao montar o relatório de problema", ex);
                    ShowProblem(T("Erro inesperado: ") + ex.Message);
                    return;
                }
                PreviewBox.Text = _package.PreviewText;
                ShowPage(preview: true);
                return;
            }

            // confirmed: save (and, in the future, send)
            SetBusy(true, T("Gerando o relatório…"));
            var progress = new Progress<OperationProgress>(p => Status.Text = p.Message);
            BugReportResult r;
            try { r = await _svc.SubmitAsync(_package, progress); }
            catch (Exception ex) { r = new BugReportResult { Outcome = ReportOutcome.Failed, Message = T("Erro inesperado: ") + ex.Message }; }
            SetBusy(false, r.Message);

            switch (r.Outcome)
            {
                case ReportOutcome.Sent:
                case ReportOutcome.SavedOffline:
                    Log.Info($"Relatório de problema gerado: {r.ZipPath ?? "(enviado)"}");
                    Status.Foreground = Ui.Res("Success");
                    if (r.ZipPath != null) Explorer.Reveal(r.ZipPath);
                    _done = true;
                    NextBtn.Content = T("Concluído");
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
            if (OnPreview && !_done) { ShowPage(preview: false); return; }
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { if (!_busy) Close(); }

        private void ShowPage(bool preview)
        {
            FormPage.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
            PreviewPage.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
            Heading.Text = preview ? T("Revisar relatório") : T("Reportar problema");
            NextBtn.Content = preview ? T("Gerar relatório") : T("Revisar");
            BackBtn.Content = preview ? T("Voltar") : T("Cancelar");
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
