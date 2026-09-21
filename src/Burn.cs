// Building a filesystem image and writing it to disc, plus read-back verification.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using STREAM = System.Runtime.InteropServices.ComTypes.IStream;

namespace Burnit
{
    /// <summary>
    /// A staged image plus every COM object that has to stay alive until the bytes
    /// have actually landed on the disc.
    /// </summary>
    public sealed class StagedImage : IDisposable
    {
        public STREAM Stream;
        public long TotalBytes;
        public int BlockSize = 2048;
        public int TotalBlocks;
        public int FileCount;
        public int DirectoryCount;
        public string VolumeName = "";

        internal object Fsi;
        internal object Result;
        internal List<object> Held = new List<object>();
        internal IDisposable SinkLink;

        public void Dispose()
        {
            if (SinkLink != null) { SinkLink.Dispose(); SinkLink = null; }
            Com.Release(Stream); Stream = null;
            Com.Release(Result); Result = null;
            Com.Release(Fsi); Fsi = null;
            foreach (object o in Held) Com.Release(o);
            Held.Clear();
        }
    }

    public static class Burn
    {
        public const int SectorBytes = 2048;

        // ---- staging --------------------------------------------------------------

        public static StagedImage BuildImage(
            dynamic recorder,
            dynamic dataFormat,
            MediaSnapshot media,
            IList<string> inputs,
            string label,
            bool append,
            FsiFileSystems filesystems,
            BurnState state)
        {
            StagedImage staged = new StagedImage();
            dynamic fsi = Com.Create("IMAPI2FS.MsftFileSystemImage");
            staged.Fsi = fsi;

            try
            {
                if (recorder != null)
                {
                    try { fsi.ChooseImageDefaults(recorder); }
                    catch (Exception) { fsi.ChooseImageDefaultsForMediaType((int)media.Type); }
                }
                else
                {
                    fsi.ChooseImageDefaultsForMediaType((int)MediaType.DvdPlusR);
                    fsi.FreeMediaBlocks = 0;     // no disc to bound us
                }

                if (append && dataFormat != null && media.Present
                    && (media.State & MediaState.NonEmptySession) != 0)
                {
                    try
                    {
                        fsi.MultisessionInterfaces = dataFormat.MultisessionInterfaces;
                        fsi.ImportFileSystem();
                        state.Detail = "importing previous session";
                    }
                    catch (Exception e)
                    {
                        throw new BurnitException("Could not import the existing session: " + Errors.Describe(e));
                    }
                }

                if (filesystems != FsiFileSystems.None)
                    fsi.FileSystemsToCreate = (int)filesystems;

                if (!string.IsNullOrEmpty(label))
                    fsi.VolumeName = SanitiseLabel(label);

                try { staged.VolumeName = (string)fsi.VolumeName; }
                catch (Exception) { }

                FsImageSink sink = new FsImageSink(state);
                staged.SinkLink = new SinkConnection((object)fsi, SinkIid.FsImage, sink);

                dynamic root = fsi.Root;
                staged.Held.Add(root);

                foreach (string raw in inputs)
                {
                    string path = Path.GetFullPath(raw);
                    if (Directory.Exists(path))
                    {
                        state.Detail = "adding  " + path;
                        root.AddTree(path, true);
                    }
                    else if (File.Exists(path))
                    {
                        state.Detail = "adding  " + Path.GetFileName(path);
                        STREAM fs = Native.OpenFileStream(path);
                        staged.Held.Add(fs);
                        root.AddFile(Path.GetFileName(path), fs);
                    }
                    else
                    {
                        throw new BurnitException("Not found: " + raw);
                    }
                }

                try { staged.FileCount = (int)fsi.FileCount; }
                catch (Exception) { }
                try { staged.DirectoryCount = (int)fsi.DirectoryCount; }
                catch (Exception) { }

                state.Detail = "building image";
                dynamic result = fsi.CreateResultImage();
                staged.Result = result;

                staged.TotalBlocks = (int)result.TotalBlocks;
                staged.BlockSize = (int)result.BlockSize;
                staged.Stream = (STREAM)result.ImageStream;
                staged.TotalBytes = (long)staged.TotalBlocks * staged.BlockSize;

                return staged;
            }
            catch (Exception)
            {
                staged.Dispose();
                throw;
            }
        }

        private static string SanitiseLabel(string label)
        {
            // Joliet tops out at 16 characters; keep all three filesystems happy.
            string s = label.Trim();
            if (s.Length > 16) s = s.Substring(0, 16);
            char[] chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] < 32 || chars[i] == '\\' || chars[i] == '/' || chars[i] == ':'
                    || chars[i] == '*' || chars[i] == '?' || chars[i] == '"' || chars[i] == '<'
                    || chars[i] == '>' || chars[i] == '|')
                    chars[i] = '_';
            return new string(chars);
        }

        // ---- writing --------------------------------------------------------------

        public static void Write(dynamic dataFormat, STREAM stream, int speedSectorsPerSec,
                                 bool closeMedia, BurnState state, DataWriteSink sink)
        {
            if (speedSectorsPerSec > 0)
            {
                try { dataFormat.SetWriteSpeed(speedSectorsPerSec, false); }
                catch (Exception) { /* drive picks its own */ }
            }
            try { dataFormat.ForceMediaToBeClosed = closeMedia; }
            catch (Exception) { }

            using (new SinkConnection((object)dataFormat, SinkIid.DataWrite, sink))
            {
                dataFormat.Write(stream);
            }
        }

        /// <summary>
        /// Walks the progress bar through a plausible burn without touching the laser.
        /// Timings come from the real image size and the real negotiated speed.
        /// </summary>
        public static void Simulate(BurnState state, long totalBytes, int speedSectorsPerSec,
                                    int sectorBytes, Func<bool> cancelled)
        {
            double bytesPerSec = (speedSectorsPerSec > 0 ? speedSectorsPerSec : 1800) * (double)sectorBytes;

            state.Mode = DiscMode.Idle;
            state.Step("validate");
            state.Detail = "validating media";
            if (Sleep(700, cancelled)) return;

            state.Step("calibrate");
            state.Detail = "calibrating laser power (OPC)";
            if (Sleep(900, cancelled)) return;

            state.Step("write");
            state.Detail = "writing";
            state.Mode = DiscMode.Writing;

            DateTime start = DateTime.UtcNow;
            double totalSeconds = totalBytes / bytesPerSec;
            state.BufferTotal = 64 * 1024 * 1024;

            while (true)
            {
                double elapsed = (DateTime.UtcNow - start).TotalSeconds;
                double f = totalSeconds <= 0 ? 1.0 : elapsed / totalSeconds;
                if (f >= 1.0) break;
                state.SetProgress((long)(totalBytes * f), totalBytes);
                state.BufferUsed = (int)(state.BufferTotal * (0.72 + 0.2 * Math.Sin(elapsed * 1.7)));
                if (Sleep(60, cancelled)) return;
            }

            state.SetProgress(totalBytes, totalBytes);
            state.Step("finalise");
            state.Detail = "closing session";
            if (Sleep(900, cancelled)) return;
            state.Mode = DiscMode.Done;
            state.Detail = "complete";
        }

        private static bool Sleep(int ms, Func<bool> cancelled)
        {
            int slept = 0;
            while (slept < ms)
            {
                if (cancelled != null && cancelled()) return true;
                Thread.Sleep(Math.Min(50, ms - slept));
                slept += 50;
            }
            return cancelled != null && cancelled();
        }

        // ---- verification ---------------------------------------------------------

        public static byte[] HashStream(Stream s, long length, BurnState state, DiscMode mode)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buf = new byte[1 << 20];
                long done = 0;
                state.Mode = mode;
                while (done < length)
                {
                    int want = (int)Math.Min(buf.Length, length - done);
                    int got = s.Read(buf, 0, want);
                    if (got <= 0) break;
                    sha.TransformBlock(buf, 0, got, null, 0);
                    done += got;
                    state.SetProgress(done, length);
                }
                sha.TransformFinalBlock(buf, 0, 0);
                return sha.Hash;
            }
        }

        /// <summary>
        /// Reads the freshly written bytes back off the disc and hashes them. The drive
        /// needs a moment to re-read the TOC after a burn, hence the retry.
        /// </summary>
        public static byte[] HashDisc(string driveLetter, long length, BurnState state)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    using (FileStream dev = Native.OpenRawDevice(driveLetter))
                    {
                        using (SHA256 sha = SHA256.Create())
                        {
                            byte[] buf = new byte[1 << 20];
                            long done = 0;
                            state.Mode = DiscMode.Reading;
                            state.SetProgress(0, length);
                            while (done < length)
                            {
                                long remain = length - done;
                                // Raw device reads must stay sector aligned.
                                int want = (int)Math.Min(buf.Length, ((remain + SectorBytes - 1) / SectorBytes) * SectorBytes);
                                int got = dev.Read(buf, 0, want);
                                if (got <= 0)
                                    throw new BurnitException("Disc ended after " + done + " bytes; expected " + length + ".");
                                int use = (int)Math.Min(got, remain);
                                sha.TransformBlock(buf, 0, use, null, 0);
                                done += use;
                                state.SetProgress(done, length);
                            }
                            sha.TransformFinalBlock(buf, 0, 0);
                            return sha.Hash;
                        }
                    }
                }
                catch (Exception e)
                {
                    last = e;
                    state.Detail = "waiting for the drive to remount the disc";
                    Thread.Sleep(2500);
                }
            }
            throw new BurnitException("Could not read the disc back for verification: "
                + (last == null ? "unknown error" : Errors.Describe(last)), last);
        }

        public static string Hex(byte[] b)
        {
            if (b == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static bool SameHash(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
