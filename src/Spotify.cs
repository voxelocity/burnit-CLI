// Burn a Spotify playlist, album or track straight to disc.
//
// spotdl does the fetching (it reads metadata from Spotify and pulls the audio
// from YouTube); burnit decodes the result to Red Book PCM and writes the CD.
// spotdl is invoked with inherited stdio so its own progress is visible live.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Burnit
{
    /// <summary>
    /// Orders "2 - x" before "10 - x", which an ordinal sort gets backwards. Playlist
    /// position is encoded in the filename, so getting this wrong reorders the album.
    /// </summary>
    internal sealed class NaturalComparer : IComparer<string>
    {
        public int Compare(string a, string b)
        {
            if (a == null) return b == null ? 0 : -1;
            if (b == null) return 1;

            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;

                    string na = a.Substring(si, i - si).TrimStart('0');
                    string nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length;
                    int c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    char ca = char.ToLowerInvariant(a[i]);
                    char cb = char.ToLowerInvariant(b[j]);
                    if (ca != cb) return ca - cb;
                    i++; j++;
                }
            }
            return (a.Length - i) - (b.Length - j);
        }
    }

    public static partial class Commands
    {
        public static int Spotify(Options o)
        {
            if (o.Inputs.Count == 0)
                throw new BurnitException(
                    "Usage: burnit spotify <playlist|album|track url or search text>...");

            if (!SpotdlPresent())
                throw new BurnitException("spotdl is not on PATH. Install it with:  pip install spotdl");

            bool keep = !string.IsNullOrEmpty(o.Keep);
            string dir = keep
                ? Path.GetFullPath(o.Keep)
                : Path.Combine(Path.GetTempPath(), "burnit-spotify-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            Out.Title("spotify");
            Out.Field("source", string.Join("  ", ToArray(o.Inputs)));
            Out.Field("download", dir + (keep ? "" : "   (temporary)"));
            if (Audio.FindFfmpeg() == null)
                Out.Warn("ffmpeg is not on PATH; spotdl needs it to produce playable audio");

            try
            {
                Directory.CreateDirectory(dir);

                // Snapshot first, so --keep into a folder that already has music only
                // burns what this run actually fetched.
                List<string> before = ListAudio(dir);

                Out.Blank();
                Out.Note("  handing off to spotdl — its output follows");
                Out.Blank();

                int code = RunSpotdl(o.Inputs, dir);
                if (code != 0)
                    throw new BurnitException("spotdl exited with code " + code + "; nothing was burned.");
                if (Program.Cancelled)
                    throw new BurnitException("Cancelled.");

                List<string> after = ListAudio(dir);
                List<string> fresh = new List<string>();
                foreach (string f in after)
                    if (!before.Contains(f)) fresh.Add(f);

                // If the folder was already populated and spotdl skipped everything as
                // already-downloaded, burn what is there rather than claiming failure.
                List<string> sources = fresh.Count > 0 ? fresh : after;
                if (sources.Count == 0)
                    throw new BurnitException("spotdl downloaded nothing into " + dir + ".");

                sources.Sort(new NaturalComparer());

                Out.Blank();
                Out.Good("downloaded " + sources.Count + " track" + (sources.Count == 1 ? "" : "s"));

                if (o.AsData)
                {
                    List<string> one = new List<string>();
                    one.Add(dir);
                    Options data = o.Clone();
                    data.Inputs = one;
                    if (string.IsNullOrEmpty(data.Label)) data.Label = "SPOTIFY";
                    return BurnData(data);
                }

                return BurnAudioFrom(o, sources, "spotify → audio cd");
            }
            finally
            {
                if (!keep)
                {
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                    catch (Exception) { }
                }
            }
        }

        private static string _spotdl;

        /// <summary>
        /// Finds spotdl on PATH ourselves. CreateProcess only ever appends ".exe", so
        /// leaving resolution to it would miss the .cmd/.bat shims that pipx and conda
        /// install, and fail with a bare "file not found".
        /// </summary>
        private static string ResolveSpotdl()
        {
            if (_spotdl != null) return _spotdl.Length == 0 ? null : _spotdl;

            string[] exts = { ".exe", ".cmd", ".bat" };
            string path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string raw in path.Split(';'))
                {
                    string dir = raw.Trim().Trim('"');
                    if (dir.Length == 0) continue;
                    foreach (string ext in exts)
                    {
                        try
                        {
                            string candidate = Path.Combine(dir, "spotdl" + ext);
                            if (File.Exists(candidate)) { _spotdl = candidate; return candidate; }
                        }
                        catch (Exception) { }
                    }
                }
            }
            _spotdl = "";
            return null;
        }

        /// <summary>Wraps a batch shim in cmd.exe, which CreateProcess cannot launch directly.</summary>
        private static ProcessStartInfo SpotdlStart(string exe, string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            string ext = Path.GetExtension(exe).ToLowerInvariant();
            if (ext == ".cmd" || ext == ".bat")
            {
                psi.FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                psi.Arguments = "/c \"\"" + exe + "\" " + args + "\"";
            }
            else
            {
                psi.FileName = exe;
                psi.Arguments = args;
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            return psi;
        }

        private static bool SpotdlPresent()
        {
            string exe = ResolveSpotdl();
            if (exe == null) return false;
            try
            {
                Process p = new Process();
                p.StartInfo = SpotdlStart(exe, "--version");
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.Start();
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(20000);
                return p.ExitCode == 0;
            }
            catch (Exception) { return false; }
        }

        private static int RunSpotdl(IList<string> inputs, string dir)
        {
            System.Text.StringBuilder args = new System.Text.StringBuilder();
            args.Append("download");
            foreach (string s in inputs)
            {
                args.Append(' ').Append('"').Append(s.Replace("\"", "")).Append('"');
            }
            // The leading list position is what preserves playlist order on the CD.
            args.Append(" --output \"").Append(dir)
                .Append(Path.DirectorySeparatorChar)
                .Append("{list-position} - {artists} - {title}.{output-ext}\"");

            Process p = new Process();
            // Stdio is inherited (not redirected), so spotdl's own progress shows live.
            p.StartInfo = SpotdlStart(ResolveSpotdl(), args.ToString());
            p.Start();

            while (!p.WaitForExit(250))
            {
                if (Program.Cancelled)
                {
                    try { p.Kill(); }
                    catch (Exception) { }
                    return 130;
                }
            }
            return p.ExitCode;
        }

        private static List<string> ListAudio(string dir)
        {
            List<string> found = new List<string>();
            if (!Directory.Exists(dir)) return found;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                if (Array.IndexOf(AudioExtensions, Path.GetExtension(f).ToLowerInvariant()) >= 0)
                    found.Add(f);
            return found;
        }

        private static string[] ToArray(IList<string> list)
        {
            string[] a = new string[list.Count];
            for (int i = 0; i < list.Count; i++) a[i] = list[i];
            return a;
        }
    }
}
