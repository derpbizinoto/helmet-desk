using System;
using System.Linq;
using System.Threading.Tasks;

namespace HelmetDesk
{
    /// <summary>Self-test checks for the sound effects (rule, embedded clips, settings). Nothing is played.</summary>
    public partial class MainWindow
    {
        private static async Task SoundTests(Action<string, bool> Check)
        {
            var today = new DateTime(2026, 3, 10);
            Item T(DateTime? date, bool done = false) => new Item { Kind = ItemKind.Task, Date = date, Done = done };

            // today / overdue form one group ("Hoje")
            var a = T(today, true); var b = T(today); var late = T(today.AddDays(-2));
            Check("som: concluir tarefa de hoje com outra pendente hoje → \"tarefa concluída\"", !SoundService.IsAllDone(a, new[] { a, b }, today));
            Check("som: tarefa atrasada pendente impede o \"tudo feito\" de hoje", !SoundService.IsAllDone(a, new[] { a, late }, today));
            Check("som: última pendente de hoje → \"tudo feito\"", SoundService.IsAllDone(a, new[] { a, T(today, true), T(today.AddDays(3)), T(null) }, today));
            var lateDone = T(today.AddDays(-1), true);
            Check("som: concluir a última atrasada (nada mais até hoje) → \"tudo feito\"", SoundService.IsAllDone(lateDone, new[] { lateDone, T(today.AddDays(1)) }, today));

            // future day: only that day counts
            var f = T(today.AddDays(5), true);
            Check("som: futura, última do dia → \"tudo feito\"", SoundService.IsAllDone(f, new[] { f, b, T(today.AddDays(6)) }, today));
            Check("som: futura com outra no mesmo dia → \"tarefa concluída\"", !SoundService.IsAllDone(f, new[] { f, T(today.AddDays(5)) }, today));

            // no date
            var n = T(null, true);
            Check("som: sem data, última sem data → \"tudo feito\"", SoundService.IsAllDone(n, new[] { n, b }, today));
            Check("som: sem data com outra sem data → \"tarefa concluída\"", !SoundService.IsAllDone(n, new[] { n, T(null) }, today));

            // notes never count; unchecked or notes never trigger
            var note = new Item { Kind = ItemKind.Note, Date = today };
            Check("som: notas não contam como pendentes", SoundService.IsAllDone(a, new[] { a, note }, today));
            Check("som: desmarcar não gera som", !SoundService.IsAllDone(T(today), new Item[0], today));

            // embedded clips decode
            SoundService.Preload();
            Check("som: os dois efeitos estão embutidos e carregam", await SoundService.Ready);

            // settings: defaults and limits (older data.json without the fields keeps the defaults)
            var s = System.Text.Json.JsonSerializer.Deserialize<Settings>("{}");
            Check("som: padrão ligado, volume 60%", s.SoundEffects && s.SoundVolume == 60 && s.SoundVolumeText == "60%");
            s.SoundVolume = 150; bool hi = s.SoundVolume == 100;
            s.SoundVolume = -5; bool lo = s.SoundVolume == 0;
            s.SoundVolume = 42.4; bool round = s.SoundVolume == 42;
            Check("som: volume limitado a 0–100 e arredondado", hi && lo && round);
        }
    }
}
