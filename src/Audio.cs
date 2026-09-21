// Audio CD support: decode anything to Red Book PCM, then write it track-at-once.
//
// Red Book is 44100 Hz, 16-bit, stereo, 2352 bytes per sector. ffmpeg does the
// decoding when it is on PATH; without it we accept WAV files that already match.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Burnit
{
    public sealed class AudioTrack
    {
        public string Source;
        public string RawPath;
        public long Bytes;
        public string Title = "";

        public int Sectors { get { return (int)(Bytes / Audio.SectorBytes); } }
        public double Seconds { get { return Bytes / (double)Audio.BytesPerSecond; } }

        public string Duration
        {
            get
            {
                int s = (int)Math.Round(Seconds);
                return (s / 60).ToString(CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
            }
        }
    }

    public static class Audio
    {
        public const int SectorBytes = 2352;
        public const int BytesPerSecond = 44100 * 2 * 2;
        public const int MinTrackSectors = 300;          // Red Book minimum: 4 seconds
        public const int GapSectors = 150;               // 2-second pause between TAO tracks

        private static string _ffmpeg;
        private static bool _ffmpegChecked;

        public static string FindFfmpeg()
        {
            if (_ffmpegChecked) return _ffmpeg;
            _ffmpegChecked = true;
            try
            {
                Process p = new Process();
                p.StartInfo.FileName = "ffmpeg";
                p.StartInfo.Arguments = "-version";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(8000);
                if (p.ExitCode == 0) _ffmpeg = "ffmpeg";
            }
            catch (Exception) { _ffmpeg = null; }
            return _ffmpeg;
        }

        public static AudioTrack Prepare(string source, string tempDir, int index, BurnState state)
        {
            if (!File.Exists(source))
                throw new BurnitException("Not found: " + source);

            AudioTrack t = new AudioTrack();
            t.Source = source;
            t.Title = Path.GetFileNameWithoutExtension(source);
            t.RawPath = Path.Combine(tempDir, "track" + index.ToString("00", CultureInfo.InvariantCulture) + ".pcm");

            state.Detail = "decoding  " + Path.GetFileName(source);

            string ff = FindFfmpeg();
            if (ff != null)
                DecodeWithFfmpeg(ff, source, t.RawPath);
            else
                ExtractCompliantWav(source, t.RawPath);

            PadTrack(t.RawPath);
            t.Bytes = new FileInfo(t.RawPath).Length;
            if (t.Bytes == 0)
                throw new BurnitException("Decoded no audio from " + Path.GetFileName(source) + ".");
            return t;
        }

        private static void DecodeWithFfmpeg(string ffmpeg, string source, string dest)
        {
            Process p = new Process();
            p.StartInfo.FileName = ffmpeg;
            p.StartInfo.Arguments = "-hide_banner -loglevel error -y -i \"" + source
                + "\" -vn -map a:0 -f s16le -acodec pcm_s16le -ar 44100 -ac 2 \"" + dest + "\"";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.CreateNoWindow = true;
            p.Start();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(dest))
                throw new BurnitException("ffmpeg could not decode " + Path.GetFileName(source)
                    + (string.IsNullOrEmpty(err) ? "." : ": " + err.Trim()));
        }

        /// <summary>Fallback when ffmpeg is absent: copy the data chunk of an already-compliant WAV.</summary>
        private static void ExtractCompliantWav(string source, string dest)
        {
            using (FileStream fs = File.OpenRead(source))
            using (BinaryReader r = new BinaryReader(fs))
            {
                if (new string(r.ReadChars(4)) != "RIFF")
                    throw new BurnitException("ffmpeg is not on PATH, so " + Path.GetFileName(source)
                        + " must be a 44.1 kHz 16-bit stereo WAV.");
                r.ReadInt32();
                if (new string(r.ReadChars(4)) != "WAVE")
                    throw new BurnitException(Path.GetFileName(source) + " is not a WAV file.");

                int channels = 0, rate = 0, bits = 0;
                while (fs.Position < fs.Length - 8)
                {
                    string id = new string(r.ReadChars(4));
                    int size = r.ReadInt32();
                    if (size < 0) break;
                    long next = fs.Position + size + (size % 2);

                    if (id == "fmt ")
                    {
                        int format = r.ReadInt16();
                        channels = r.ReadInt16();
                        rate = r.ReadInt32();
                        r.ReadInt32();
                        r.ReadInt16();
                        bits = r.ReadInt16();
                        if (format != 1 || channels != 2 || rate != 44100 || bits != 16)
                            throw new BurnitException(Path.GetFileName(source) + " is "
                                + rate + " Hz / " + bits + "-bit / " + channels + "ch. Install ffmpeg, "
                                + "or supply 44100 Hz 16-bit stereo WAV.");
                    }
                    else if (id == "data")
                    {
                        using (FileStream outFs = File.Create(dest))
                        {
                            byte[] buf = new byte[1 << 20];
                            int left = size;
                            while (left > 0)
                            {
                                int got = fs.Read(buf, 0, Math.Min(buf.Length, left));
                                if (got <= 0) break;
                                outFs.Write(buf, 0, got);
                                left -= got;
                            }
                        }
                        return;
                    }
                    fs.Position = next;
                }
                throw new BurnitException("No audio data found in " + Path.GetFileName(source) + ".");
            }
        }

        /// <summary>Pads with silence to a whole sector and to the 4-second Red Book minimum.</summary>
        private static void PadTrack(string path)
        {
            FileInfo fi = new FileInfo(path);
            long len = fi.Length;
            long minBytes = (long)MinTrackSectors * SectorBytes;
            long target = Math.Max(len, minBytes);
            if (target % SectorBytes != 0)
                target += SectorBytes - (target % SectorBytes);
            if (target == len) return;

            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                fs.Seek(0, SeekOrigin.End);
                byte[] silence = new byte[SectorBytes];
                long left = target - len;
                while (left > 0)
                {
                    int n = (int)Math.Min(silence.Length, left);
                    fs.Write(silence, 0, n);
                    left -= n;
                }
            }
        }

        public static int TotalSectors(IList<AudioTrack> tracks)
        {
            int total = 0;
            for (int i = 0; i < tracks.Count; i++)
            {
                total += tracks[i].Sectors;
                if (i > 0) total += GapSectors;
            }
            return total;
        }

        public static string TotalDuration(IList<AudioTrack> tracks)
        {
            double s = 0;
            foreach (AudioTrack t in tracks) s += t.Seconds;
            int sec = (int)Math.Round(s);
            return (sec / 60).ToString(CultureInfo.InvariantCulture) + ":" + (sec % 60).ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
