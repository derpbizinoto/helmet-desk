using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HelmetDesk
{
    /// <summary>
    /// The app's only two sounds: a task was completed, and the completed task was the last one pending ("tudo feito").
    /// The WAVs are embedded (Assets/Sounds) and decoded once in the background at startup. Playback runs off the UI
    /// thread with NAudio (volume from the settings); a new sound stops the previous one, so fast clicks never pile
    /// up. The output device is only open while a sound plays. Any failure is logged and the app stays silent.
    /// </summary>
    public static class SoundService
    {
        private sealed class Clip
        {
            public byte[] Data;
            public WaveFormat Format;
        }

        private static Task<bool> _ready = Task.FromResult(false);
        private static Clip _taskCompleted, _allDone;
        private static readonly object Gate = new();
        private static WaveOutEvent _out;
        private static long _lastStart;

        /// <summary>No sound at all (self-test and snapshots).</summary>
        public static bool Muted { get; set; }

        /// <summary>True once both sounds are decoded (false if they could not be read).</summary>
        internal static Task<bool> Ready => _ready;

        /// <summary>Called at startup; returns immediately.</summary>
        public static void Preload()
        {
            if (_ready.IsCompleted && _ready.Result) return;
            _ready = Task.Run(() =>
            {
                try
                {
                    _taskCompleted = Load("tarefa-concluida.wav");
                    _allDone = Load("tudo-feito.wav");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warn("som: não foi possível carregar os efeitos sonoros", ex);
                    return false;
                }
            });
        }

        private static Clip Load(string name)
        {
            using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("Sounds." + name)
                ?? throw new FileNotFoundException("recurso de som ausente: " + name);
            using var reader = new WaveFileReader(res);
            using var ms = new MemoryStream();
            reader.CopyTo(ms);
            return new Clip { Data = ms.ToArray(), Format = reader.WaveFormat };
        }

        public static void PlayTaskCompleted() => Play(() => _taskCompleted);
        public static void PlayAllDone() => Play(() => _allDone);

        /// <summary>
        /// A task was just checked by the user: plays "tudo feito" when it left nothing pending in its group
        /// (see <see cref="IsAllDone"/>), otherwise "tarefa concluída". Never both.
        /// </summary>
        public static void OnTaskChecked(Item it)
        {
            if (it == null || !it.IsTask || !it.Done) return;
            if (IsAllDone(it, Store.Data.Items, DateTime.Today)) PlayAllDone();
            else PlayTaskCompleted();
        }

        /// <summary>
        /// "Tudo feito" rule, for the task that was just completed (only tasks count, notes never):
        /// date today or earlier → no pending task dated today or earlier is left (the "Hoje" list, overdue included);
        /// future date → no pending task left on that same day; no date → no pending task without a date is left.
        /// </summary>
        public static bool IsAllDone(Item done, IEnumerable<Item> items, DateTime today)
        {
            if (done == null || !done.IsTask || !done.Done) return false;
            var pending = items.Where(i => i.IsTask && !i.Done && i != done);
            if (done.Date == null) return !pending.Any(i => i.Date == null);
            if (done.Date.Value.Date <= today.Date) return !pending.Any(i => i.Date.HasValue && i.Date.Value.Date <= today.Date);
            return !pending.Any(i => i.Date.HasValue && i.Date.Value.Date == done.Date.Value.Date);
        }

        private static void Play(Func<Clip> pick)
        {
            var s = Store.Data.Settings;
            if (Muted || !s.SoundEffects || s.SoundVolume <= 0) return;
            float volume = (float)Math.Clamp(s.SoundVolume / 100.0, 0, 1);
            var ready = _ready;
            Task.Run(async () =>
            {
                try
                {
                    if (!await ready) return;
                    var clip = pick();
                    if (clip != null) Start(clip, volume);
                }
                catch (Exception ex) { Log.Warn("som: falha ao tocar", ex); }
            });
        }

        private static void Start(Clip clip, float volume)
        {
            lock (Gate)
            {
                // two checks within a few ms (e.g. a double click) play once
                long now = Environment.TickCount64;
                if (now - _lastStart < 60) return;
                _lastStart = now;

                // restart instead of stacking: the previous sound stops
                var old = _out;
                _out = null;
                old?.Dispose();

                var stream = new RawSourceWaveStream(new MemoryStream(clip.Data, false), clip.Format);
                var provider = new VolumeSampleProvider(stream.ToSampleProvider()) { Volume = volume };
                var output = new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 2 };
                output.PlaybackStopped += (o, e) =>
                {
                    if (e.Exception != null) Log.Warn("som: reprodução interrompida", e.Exception);
                    // close the device once the sound ends (never from its own playback thread)
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        lock (Gate)
                        {
                            if (_out != output) return;
                            _out = null;
                        }
                        output.Dispose();
                        stream.Dispose();
                    });
                };
                output.Init(provider);
                _out = output;
                output.Play();
            }
        }
    }
}
