using System;
using System.Diagnostics;
using System.IO;

namespace HelmetDesk
{
    /// <summary>Opens folders and files in Windows Explorer. A failure is only logged: it never shows an error to the user.</summary>
    public static class Explorer
    {
        /// <summary>Opens <paramref name="dir"/> (created first if it does not exist yet).</summary>
        public static void OpenFolder(string dir)
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { Log.Warn("Não foi possível criar a pasta para abrir no Explorer", ex); }
            Start($"\"{dir}\"");
        }

        /// <summary>Opens the file's folder with the file selected.</summary>
        public static void Reveal(string path) => Start($"/select,\"{path}\"");

        private static void Start(string args)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("Não foi possível abrir o Explorer", ex); }
        }
    }
}
