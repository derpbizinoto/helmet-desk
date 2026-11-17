using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HelmetDesk
{
    /// <summary>Self-test checks and snapshots for links in descriptions and note attachments (no network).</summary>
    public partial class MainWindow
    {
        private static int ChipCount(FlowDocument doc) =>
            doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<InlineUIContainer>().Count(c => c.Child is Border { Tag: string });

        private async Task AttachTests(Action<string, bool> Check)
        {
            // optional real-network probe (developer only): HELMETDESK_LINKPROBE=<url>
            if (Environment.GetEnvironmentVariable("HELMETDESK_LINKPROBE") is { Length: > 0 } probe)
            {
                LinkPreview.Enabled = true;
                LinkPreview.Get(probe);
                for (int i = 0; i < 150 && LinkPreview.Get(probe) == null; i++) await Task.Delay(100);
                var e = LinkPreview.Get(probe);
                Check($"links (rede): título \"{LinkPreview.FullTitle(probe)}\" → etiqueta \"{LinkPreview.Label(probe)}\", ícone {(LinkPreview.Icon(probe) != null ? "ok" : "ausente")}",
                    e != null && !e.Failed && LinkPreview.Label(probe) != LinkPreview.Host(probe) && LinkPreview.Icon(probe) != null);
            }
            LinkPreview.Enabled = false;
            var n = Store.NewItem(ItemKind.Note);
            n.Title = "Teste de anexos";
            var task = Store.NewItem(ItemKind.Task);
            try
            {
                Refresh(false);
                OpenItem(n, true);
                await Snapshots.Idle(Dispatcher);

                // ----- links in the description -----
                var body = "Ver https://pt.wikipedia.org/wiki/Escitalopram e www.exemplo.com.br/a.\nLinha 2";
                n.Body = body;
                await Snapshots.Idle(Dispatcher);
                Check("links: endereços da descrição viram etiquetas", ChipCount(EdBody.Document) == 2);
                Check("links: texto salvo continua com o endereço completo", EdBody.Text == body && n.Body == body);
                Check("links: ponto final não entra no link", LinkText.Split(body).Count(p => p.url && p.text == "www.exemplo.com.br/a") == 1);
                EdBody.Document.Blocks.LastBlock.ContentEnd.InsertTextInRun(" https://github.com");
                EdBody.ConvertUrls();
                await Snapshots.Idle(Dispatcher);
                Check("links: link digitado/colado depois também vira etiqueta e é salvo",
                    ChipCount(EdBody.Document) == 3 && n.Body == body + " https://github.com");
                LinkPreview.Seed("https://github.com", "GitHub · Build and ship software");
                Check("links: etiqueta usa a primeira parte do título da página", LinkPreview.Label("https://github.com") == "GitHub");
                Check("links: sem título, etiqueta mostra o domínio", LinkPreview.Label("https://www.exemplo.com.br/a") == "exemplo.com.br");
                Check("links: prévia do card troca o endereço pelo nome", n.Preview.Contains("🔗 GitHub") && !n.Preview.Contains("github.com"));
                EdBody.Selection.Select(EdBody.Document.ContentStart, EdBody.Document.ContentEnd);
                { var ser = LinkBox.Serialize(EdBody.Selection.Start, EdBody.Selection.End); Check("links: copiar a descrição devolve os endereços" + (ser == n.Body ? "" : " [" + ser.Replace("\n", "/") + "] vs [" + n.Body.Replace("\n", "/") + "]"), ser == n.Body); }

                // ----- attachments -----
                var px = new byte[16 * 16 * 4];
                for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 80; px[i + 2] = 140; px[i + 3] = 255; }
                var bmp = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, px, 64);
                var img = new DataObject(); img.SetImage(bmp);
                Check("anexos: seção aparece na nota em edição", EdAttach.Visibility == Visibility.Visible && EdAttach.Children.OfType<Border>().Any(b => b.Name == "AttachZone"));
                Check("anexos: imagem colada (Ctrl+V) vira anexo salvo na pasta de dados",
                    PasteInto(n, img, false, false) && n.Attachments.Count == 1 && n.Attachments[0].Kind == AttachmentKind.Image
                    && File.Exists(Attachments.FullPath(n.Attachments[0])) && Attachments.FullPath(n.Attachments[0]).StartsWith(Attachments.Root));
                Check("anexos: imagem colada mesmo com o cursor na descrição vai para os anexos", PasteInto(n, img, true, false) && n.Attachments.Count == 2);
                n.Attachments.RemoveAt(1);
                var url = new DataObject(DataFormats.UnicodeText, "https://www.youtube.com/watch?v=abc123");
                Check("anexos: link colado fora da descrição vira anexo", PasteInto(n, url, false, false) && n.Attachments.Count == 2 && n.Attachments[1].Kind == AttachmentKind.Link);
                Check("anexos: link colado na descrição fica na descrição", !PasteInto(n, url, true, true) && n.Attachments.Count == 2);
                Check("anexos: texto comum cola normalmente", !PasteInto(n, new DataObject(DataFormats.UnicodeText, "texto comum"), false, true) && n.Attachments.Count == 2);
                var mixed = new DataObject(); mixed.SetImage(bmp); mixed.SetText("A1\tB1");
                Check("anexos: células do Excel (texto + imagem) num campo de texto colam como texto", !PasteInto(n, mixed, false, true) && n.Attachments.Count == 2);

                var file = Path.Combine(AppPaths.DataDir, "teste-anexo.txt");
                File.WriteAllText(file, "conteúdo de teste");
                Check("anexos: arquivo colado é aceito", PasteInto(n, new DataObject(DataFormats.FileDrop, new[] { file }), false, false));
                for (int i = 0; i < 40 && n.Attachments.Count < 3; i++) await Task.Delay(50);
                var fa = n.Attachments.LastOrDefault();
                Check("anexos: arquivo é copiado (o original pode ser apagado)", n.Attachments.Count == 3 && fa?.Kind == AttachmentKind.File
                    && File.Exists(Attachments.FullPath(fa)) && Attachments.FullPath(fa) != file && fa.Size == new FileInfo(file).Length);
                File.Delete(file);
                await Snapshots.Idle(Dispatcher);
                Check("anexos: imagens em miniatura e demais itens em lista",
                    EdAttach.Children.OfType<WrapPanel>().SingleOrDefault()?.Children.Count == 1 && EdAttach.Children.OfType<Border>().Count(b => b.Name != "AttachZone") == 2);
                Check("anexos: tarefa não recebe anexos ao colar", !PasteInto(task, img, false, false) && task.Attachments.Count == 0);

                var imgAtt = n.Attachments[0];
                var imgPath = Attachments.FullPath(imgAtt);
                RemoveAttachment(n, imgAtt);
                Check("anexos: remover tira da nota mas mantém o arquivo (para desfazer)", n.Attachments.Count == 2 && File.Exists(imgPath));
                SnackAction_Click(null, null);
                Check("anexos: desfazer devolve o anexo no lugar", n.Attachments.Count == 3 && n.Attachments[0] == imgAtt);

                OpenItem(n, false);
                await Snapshots.Idle(Dispatcher);
                Check("anexos: aparecem também no modo leitura", ViewAttach.Visibility == Visibility.Visible && ViewAttach.Children.Count >= 3);

                // duplicate copies the files; startup cleanup removes only what nobody uses
                OpenItem(n, true);
                Duplicate_Click(null, null);
                var copy = _editing;
                Check("anexos: duplicar copia os arquivos para a nova nota", copy != n && copy.Attachments.Count == 3
                    && copy.Attachments.Where(a => a.File != null).All(a => File.Exists(Attachments.FullPath(a)) && Attachments.FullPath(a) != Attachments.FullPath(n.Attachments.First(o => o.Name == a.Name))));
                n.Attachments.Remove(imgAtt);
                Attachments.Purge(Store.Data.Items.ToList(), DateTime.Now.AddSeconds(5));
                Check("anexos: limpeza apaga só arquivos sem uso", !File.Exists(imgPath) && n.Attachments.Where(a => a.File != null).All(a => File.Exists(Attachments.FullPath(a))));
                Store.Data.Items.Remove(copy);
                Attachments.Purge(Store.Data.Items.ToList(), DateTime.Now.AddSeconds(5));
                Check("anexos: nota excluída tem a pasta limpa", !Directory.Exists(Path.Combine(Attachments.Root, copy.Id)));

                Store.Save();
                var json = File.ReadAllText(AppPaths.DataFile);
                Check("anexos: salvos no data.json", json.Contains("\"Attachments\"") && json.Contains("youtube.com/watch?v=abc123"));
                var bz = Path.Combine(AppPaths.LocalDir, "teste-backup-anexos.zip");
                Directory.CreateDirectory(AppPaths.LocalDir);
                await CreateBackupService().ExportAsync(bz);
                using (var z = System.IO.Compression.ZipFile.OpenRead(bz))
                    Check("anexos: entram no backup .zip", n.Attachments.Count(a => a.File != null) >= 1
                        && n.Attachments.Where(a => a.File != null).All(a => z.GetEntry(a.File.Replace('\\', '/')) != null));
                File.Delete(bz);
            }
            finally
            {
                CloseEditor(false);
                HideSnack();
                Store.Data.Items.Remove(n);
                Store.Data.Items.Remove(task);
                Refresh(false);
            }
        }

        private async Task AttachShots(Func<string, Task> Shot, string dir)
        {
            LinkPreview.Enabled = false;
            LinkPreview.Seed("https://pt.wikipedia.org/wiki/Escitalopram", "Escitalopram – Wikipédia, a enciclopédia livre");
            LinkPreview.Seed("https://www.drogaosuper.com.br/melatonina", "Melatonina 0,21 mg | Drogão Super");
            LinkPreview.Seed("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Como organizar remédios da semana - YouTube");
            var n = Store.NewItem(ItemKind.Note);
            n.Title = "Próxima farmácia";
            n.Body = "Conferir preço: https://www.drogaosuper.com.br/melatonina\nBula: https://pt.wikipedia.org/wiki/Escitalopram";
            foreach (var t in new[] { "Escitalopram (Drogão)", "Risperidona (São Paulo)", "Hidratante (São Paulo)" }) n.Checklist.Add(new CheckItem { Text = t });
            try
            {
                foreach (var shot in new[] { "1-hoje.png", "2-notas.png" })
                {
                    var src = Path.Combine(dir, shot);
                    if (File.Exists(src)) await Attachments.AddFilesAsync(n, new[] { src });
                }
                var pdf = Path.Combine(AppPaths.DataDir, "Receita - dra. Ana.pdf");
                File.WriteAllBytes(pdf, new byte[184_320]);
                await Attachments.AddFilesAsync(n, new[] { pdf });
                File.Delete(pdf);
                Attachments.AddLink(n, "https://www.youtube.com/watch?v=dQw4w9WgXcQ");
                Refresh(false);
                NavNotes.IsChecked = true;
                OpenItem(n, true);
                await Shot("20a-nota-links-anexos");
                EdScroll.ScrollToEnd();
                await Shot("20b-nota-anexos-fim");
                OpenItem(n, false);
                await Shot("20c-nota-anexos-leitura");
            }
            finally
            {
                CloseEditor(false);
                Store.Data.Items.Remove(n);
                NavToday.IsChecked = true;
                Refresh(false);
            }
        }
    }
}
