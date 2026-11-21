using System;
using System.IO;
using System.Text.Json;

namespace HelmetDesk.Core
{
    /// <summary>
    /// Switches for experimental panels, read once at startup from experimental.json in the data folder
    /// (<see cref="AppPaths.ExperimentalFile"/>). Without that file, or with anything it can't read, every switch is off
    /// and the app behaves exactly as without the experiment. Example: <c>{ "conteudo": true }</c>.
    /// The app never writes this file: the user creates it to opt in.
    /// </summary>
    public static class Experimental
    {
        private static bool? _content;

        /// <summary>Painel "Conteúdo" (esteira de produção).</summary>
        public static bool Content => _content ??= ReadFlag(AppPaths.ExperimentalFile, "conteudo");

        /// <summary>True only when <paramref name="path"/> is a JSON object whose <paramref name="name"/> is <c>true</c>.</summary>
        public static bool ReadFlag(string path, string name)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(name, out var v)
                    && v.ValueKind == JsonValueKind.True;
            }
            catch (Exception) { return false; }
        }
    }
}
