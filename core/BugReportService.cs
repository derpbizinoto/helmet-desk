using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HelmetDesk.Core
{
    /// <summary>What the user typed in the "Reportar problema" form.</summary>
    public sealed class BugReportInput
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Email { get; set; } = "";
        public bool IncludeLogs { get; set; } = true;
        /// <summary>Off unless the user ticks it: notes and tasks are private.</summary>
        public bool IncludeUserData { get; set; }
        /// <summary>Filled when the form is opened after a crash.</summary>
        public string Exception { get; set; }
    }

    /// <summary>Everything that will leave the app, built once so the preview shows exactly what is sent.</summary>
    public sealed class BugReportPackage
    {
        public string Id { get; init; }
        public string ReportText { get; init; }
        public string ReportJson { get; init; }
        /// <summary>Extra files attached (name inside the zip → full path on disk). Only the user's data, and only if allowed.</summary>
        public IReadOnlyDictionary<string, string> Attachments { get; init; }

        /// <summary>The preview: the report itself, plus the list of what goes in the package.</summary>
        public string PreviewText
        {
            get
            {
                var sb = new StringBuilder(ReportText);
                sb.AppendLine();
                sb.AppendLine("──────── Arquivos no pacote ────────");
                sb.AppendLine("report.txt  (este texto)");
                sb.AppendLine("report.json (o mesmo conteúdo, em formato estruturado)");
                foreach (var a in Attachments) sb.AppendLine($"{a.Key}  (seus dados)");
                return sb.ToString();
            }
        }
    }

    public enum ReportOutcome { Sent, SavedOffline, RateLimited, Invalid, Failed }

    public sealed class BugReportResult
    {
        public ReportOutcome Outcome { get; init; }
        public string Message { get; init; }
        /// <summary>The .zip saved to disk (offline fallback).</summary>
        public string ZipPath { get; init; }
        public TimeSpan RetryAfter { get; init; }
    }

    /// <summary>An online destination (e.g. a Cloudflare Worker). None is configured yet: reports are saved as .zip.</summary>
    public interface IReportTransport
    {
        Task SendAsync(BugReportPackage package, CancellationToken ct);
    }

    public sealed class BugReportOptions
    {
        public string AppVersion { get; set; } = "0.0.0";
        public string LogDir { get; set; }
        public int LogLines { get; set; } = 200;
        /// <summary>Folder where the offline .zip packages go.</summary>
        public string OutputDir { get; set; }
        /// <summary>The user's data files, attached only when <see cref="BugReportInput.IncludeUserData"/> is set.</summary>
        public IReadOnlyList<string> UserDataFiles { get; set; } = Array.Empty<string>();
        public TimeSpan MinInterval { get; set; } = TimeSpan.FromMinutes(1);
        /// <summary>Text replaced in logs and errors before anything is shown or saved (e.g. the user's profile path → %USERPROFILE%).</summary>
        public IReadOnlyDictionary<string, string> Redactions { get; set; } = new Dictionary<string, string>();
        public int KeepPackages { get; set; } = 10;
    }

    public interface IBugReportService
    {
        string Validate(BugReportInput input);
        BugReportPackage Build(BugReportInput input);
        Task<BugReportResult> SubmitAsync(BugReportPackage package, IProgress<OperationProgress> progress = null, CancellationToken ct = default);
    }

    public sealed class BugReportService : IBugReportService
    {
        private readonly BugReportOptions _o;
        private readonly IClock _clock;
        private readonly IReportTransport _online;
        private DateTimeOffset? _last;
        private readonly object _gate = new();

        public BugReportService(BugReportOptions options, IClock clock = null, IReportTransport online = null)
        {
            _o = options ?? throw new ArgumentNullException(nameof(options));
            _clock = clock ?? SystemClock.Instance;
            _online = online;
        }

        /// <summary>Null when the form is fine, otherwise the message to show.</summary>
        public string Validate(BugReportInput input)
        {
            if (string.IsNullOrWhiteSpace(input?.Title)) return "Dê um título ao problema.";
            if (string.IsNullOrWhiteSpace(input.Description)) return "Descreva o que aconteceu.";
            var mail = input.Email?.Trim() ?? "";
            if (mail.Length > 0 && (!mail.Contains('@') || mail.StartsWith("@") || mail.EndsWith("@") || mail.Contains(' ')))
                return "O e-mail não parece válido (ou deixe em branco).";
            return null;
        }

        public BugReportPackage Build(BugReportInput input)
        {
            var info = SystemInfo.Collect(_o.AppVersion, _clock);
            var logs = input.IncludeLogs ? LogTail.Read(_o.LogDir, _o.LogLines).Select(Redact).ToList() : new List<string>();
            input = new BugReportInput
            {
                Title = input.Title ?? "", Description = input.Description ?? "", Email = input.Email,
                IncludeLogs = input.IncludeLogs, IncludeUserData = input.IncludeUserData, Exception = Redact(input.Exception),
            };
            var id = _clock.Now.ToString("yyyyMMdd-HHmmss");

            var sb = new StringBuilder();
            sb.AppendLine("RELATÓRIO DE PROBLEMA — HELMET DESK");
            sb.AppendLine();
            sb.AppendLine($"Título: {input.Title.Trim()}");
            sb.AppendLine($"Contato: {(string.IsNullOrWhiteSpace(input.Email) ? "(não informado)" : input.Email.Trim())}");
            sb.AppendLine();
            sb.AppendLine("Descrição:");
            sb.AppendLine(input.Description.Trim());
            sb.AppendLine();
            sb.AppendLine("──────── Sistema ────────");
            sb.Append(info.ToText());
            if (!string.IsNullOrWhiteSpace(input.Exception))
            {
                sb.AppendLine();
                sb.AppendLine("──────── Erro capturado ────────");
                sb.AppendLine(input.Exception.Trim());
            }
            sb.AppendLine();
            if (input.IncludeLogs)
            {
                sb.AppendLine($"──────── Log técnico (últimas {logs.Count} linhas) ────────");
                foreach (var l in logs) sb.AppendLine(l);
                if (logs.Count == 0) sb.AppendLine("(nenhum registro)");
            }
            else sb.AppendLine("Log técnico: não incluído.");
            sb.AppendLine();
            sb.AppendLine(input.IncludeUserData ? "Dados do usuário: incluídos (anexo)." : "Dados do usuário: não incluídos.");

            var attachments = new Dictionary<string, string>();
            if (input.IncludeUserData)
                foreach (var f in _o.UserDataFiles.Where(File.Exists))
                    attachments["dados/" + Path.GetFileName(f)] = f;

            var json = JsonSerializer.Serialize(new
            {
                title = input.Title.Trim(),
                description = input.Description.Trim(),
                email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim(),
                exception = string.IsNullOrWhiteSpace(input.Exception) ? null : input.Exception.Trim(),
                system = info,
                logs = input.IncludeLogs ? logs : null,
                userDataIncluded = input.IncludeUserData,
            }, new JsonSerializerOptions { WriteIndented = true });

            return new BugReportPackage { Id = id, ReportText = sb.ToString(), ReportJson = json, Attachments = attachments };
        }

        private string Redact(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            foreach (var (k, v) in _o.Redactions.Where(r => !string.IsNullOrEmpty(r.Key)).OrderByDescending(r => r.Key.Length))
                s = s.Replace(k, v, StringComparison.OrdinalIgnoreCase);
            return s;
        }

        public Task<BugReportResult> SubmitAsync(BugReportPackage package, IProgress<OperationProgress> progress = null, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var now = _clock.Now;
                if (_last is DateTimeOffset last && now - last < _o.MinInterval)
                {
                    var wait = _o.MinInterval - (now - last);
                    return Task.FromResult(new BugReportResult
                    {
                        Outcome = ReportOutcome.RateLimited,
                        RetryAfter = wait,
                        Message = $"Aguarde {Math.Ceiling(wait.TotalSeconds):0} s para enviar outro relatório."
                    });
                }
                _last = now;
            }
            return SubmitCore(package, progress, ct);
        }

        private async Task<BugReportResult> SubmitCore(BugReportPackage package, IProgress<OperationProgress> progress, CancellationToken ct)
        {
            string onlineError = null;
            if (_online != null)
            {
                progress?.Report(new(20, "Enviando…"));
                try
                {
                    await _online.SendAsync(package, ct).ConfigureAwait(false);
                    progress?.Report(new(100, "Enviado."));
                    return new BugReportResult { Outcome = ReportOutcome.Sent, Message = "Relatório enviado. Obrigado!" };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { onlineError = ex.Message; }
            }

            // offline fallback: a .zip the user can attach to an e-mail
            progress?.Report(new(60, "Gerando o pacote…"));
            try
            {
                var path = await Task.Run(() => SaveZip(package), ct).ConfigureAwait(false);
                progress?.Report(new(100, "Pacote gerado."));
                return new BugReportResult
                {
                    Outcome = ReportOutcome.SavedOffline,
                    ZipPath = path,
                    Message = onlineError == null
                        ? "Relatório salvo. Anexe o arquivo selecionado num e-mail para o desenvolvedor."
                        : $"Não foi possível enviar ({onlineError}). O relatório foi salvo: anexe o arquivo selecionado num e-mail."
                };
            }
            catch (Exception ex)
            {
                lock (_gate) _last = null; // nothing went out: let the user try again right away
                return new BugReportResult { Outcome = ReportOutcome.Failed, Message = "Não foi possível gerar o relatório: " + ex.Message };
            }
        }

        private string SaveZip(BugReportPackage p)
        {
            Directory.CreateDirectory(_o.OutputDir);
            var path = Path.Combine(_o.OutputDir, $"HelmetDesk-relatorio-{p.Id}.zip");
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Write(zip, "report.txt", p.ReportText);
                Write(zip, "report.json", p.ReportJson);
                foreach (var a in p.Attachments)
                {
                    var e = zip.CreateEntry(a.Key, CompressionLevel.Optimal);
                    using var dst = e.Open();
                    using var src = new FileStream(a.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    src.CopyTo(dst);
                }
            }
            File.Move(tmp, path, true);
            try
            {
                foreach (var old in new DirectoryInfo(_o.OutputDir).GetFiles("HelmetDesk-relatorio-*.zip")
                             .OrderByDescending(f => f.Name).Skip(_o.KeepPackages))
                    old.Delete();
            }
            catch { }
            return path;
        }

        private static void Write(ZipArchive zip, string name, string text)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var s = new StreamWriter(e.Open(), new UTF8Encoding(false));
            s.Write(text);
        }
    }
}
