// The verbs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using STREAM = System.Runtime.InteropServices.ComTypes.IStream;

namespace Burnit
{
    public static class Out
    {
        public static bool Colour = true;

        private static string C(int rgb, string s)
        {
            return Colour ? Ansi.Fg(rgb) + s + Ansi.Reset : s;
        }

        public static void Blank() { Console.WriteLine(); }

        public static void Title(string s)
        {
            Theme t = Program.ActiveTheme;
            Console.WriteLine();
            Console.WriteLine("  " + C(t.Accent, s));
            Console.WriteLine();
        }

        public static void Field(string label, string value)
        {
            Theme t = Program.ActiveTheme;
            Console.WriteLine("  " + C(t.Label, label.PadRight(9)) + C(t.Value, value));
        }

        public static void Line(string s)
        {
            Console.WriteLine("  " + s);
        }

        public static void Note(string s)
        {
            Console.WriteLine("  " + C(Program.ActiveTheme.Dim, s));
        }

        public static void Good(string s)
        {
            Console.WriteLine("  " + C(Rgb.Make(126, 196, 116), "✓ ") + s);
        }

        public static void Warn(string s)
        {
            Console.WriteLine("  " + C(Rgb.Make(232, 186, 84), "! ") + s);
        }

        public static void Bad(string s)
        {
            Console.Error.WriteLine("  " + C(Rgb.Make(224, 104, 84), "✗ ") + s);
        }

        public static bool Confirm(string question, bool assumeYes)
        {
            if (assumeYes) return true;
            if (Console.IsInputRedirected)
                throw new BurnitException("Refusing to run unattended without --yes.");
            Console.WriteLine();
            Console.Write("  " + question + " [y/N] ");
            string answer = Console.ReadLine();
            Console.WriteLine();
            return answer != null && (answer.Trim().ToLowerInvariant() == "y" || answer.Trim().ToLowerInvariant() == "yes");
        }
    }

    public static class Commands
    {
        // ---- drives ---------------------------------------------------------------

        public static int Drives(Options o)
        {
            List<RecorderInfo> all = Devices.Enumerate();
            Out.Title("optical recorders");
            if (all.Count == 0)
            {
                Out.Note("none found");
                return 1;
            }

            foreach (RecorderInfo r in all)
            {
                string letter = r.Letter.Length > 0 ? r.Letter : "--";
                Console.WriteLine("  [" + r.Index + "]  " + letter + "  " + r.Name
                    + (r.Revision.Length > 0 ? "  (" + r.Revision + ")" : ""));

                dynamic rec = null;
                try
                {
                    rec = Devices.OpenRecorder(r);
                    MediaSnapshot m = Devices.Inspect(rec);
                    string media = m.Present
                        ? Media.Describe(m.Type) + " · " + m.StateText + " · "
                          + Fmt.Bytes(m.FreeBytes) + " free"
                        : "no disc";
                    Out.Note("       " + media);
                }
                catch (Exception e) { Out.Note("       " + Errors.Describe(e)); }
                finally { Com.Release(rec); }
            }
            Out.Blank();
            return 0;
        }

        // ---- info -----------------------------------------------------------------

        public static int Info(Options o)
        {
            RecorderInfo info = Devices.Select(o.Drive);
            dynamic rec = Devices.OpenRecorder(info);
            try
            {
                MediaSnapshot m = Devices.Inspect(rec);
                Out.Title("disc in " + (info.Letter.Length > 0 ? info.Letter : "drive " + info.Index));

                Out.Field("drive", info.Name + (info.Revision.Length > 0 ? "  rev " + info.Revision : ""));
                if (!m.Present)
                {
                    Out.Field("media", m.Problem ?? "no disc loaded");
                    Out.Blank();
                    return 1;
                }

                Out.Field("media", Media.Describe(m.Type)
                    + (Media.IsRewritable(m.Type) ? "  (rewritable)" : ""));
                Out.Field("state", m.StateText);
                Out.Field("capacity", Fmt.Bytes(m.CapacityBytes) + "  ·  "
                    + m.TotalSectors.ToString("N0", CultureInfo.InvariantCulture) + " sectors");
                Out.Field("free", Fmt.Bytes(m.FreeBytes) + "  ·  "
                    + m.FreeSectors.ToString("N0", CultureInfo.InvariantCulture) + " sectors");
                if (m.NextWritable > 0)
                    Out.Field("next lba", m.NextWritable.ToString("N0", CultureInfo.InvariantCulture));

                if (m.SupportedSpeeds.Length > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    int baseRate = Media.BaseSectorsPerSecond(m.Type);
                    for (int i = m.SupportedSpeeds.Length - 1; i >= 0; i--)
                    {
                        if (sb.Length > 0) sb.Append(", ");
                        double x = (double)m.SupportedSpeeds[i] / baseRate;
                        sb.Append(x.ToString(x >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture)).Append('x');
                    }
                    Out.Field("speeds", sb.ToString());
                }
                if (m.CurrentSpeed > 0)
                    Out.Field("current", Devices.SpeedText(m.CurrentSpeed, m.Type));

                if (Media.IsCd(m.Type))
                {
                    double minutes = m.TotalSectors / 75.0 / 60.0;
                    Out.Field("audio", minutes.ToString("0.0", CultureInfo.InvariantCulture) + " minutes if written as an audio CD");
                }

                Out.Blank();
                return 0;
            }
            finally { Com.Release(rec); }
        }

        // ---- eject / close --------------------------------------------------------

        public static int Tray(Options o, bool open)
        {
            RecorderInfo info = Devices.Select(o.Drive);
            dynamic rec = Devices.OpenRecorder(info);
            try
            {
                if (open) rec.EjectMedia(); else rec.CloseTray();
                Out.Blank();
                Out.Good((open ? "ejected " : "closed tray on ") + (info.Letter.Length > 0 ? info.Letter : info.Name));
                Out.Blank();
                return 0;
            }
            finally { Com.Release(rec); }
        }

        // ---- image (no disc involved) ---------------------------------------------

        public static int Image(Options o)
        {
            if (o.Inputs.Count < 2)
                throw new BurnitException("Usage: burnit image <out.iso> <file-or-folder>...");

            string outPath = Path.GetFullPath(o.Inputs[0]);
            List<string> sources = o.Inputs.GetRange(1, o.Inputs.Count - 1);

            if (File.Exists(outPath) && !o.Yes)
            {
                if (!Out.Confirm(Path.GetFileName(outPath) + " exists. Overwrite?", false))
                    return 1;
            }

            BurnState state = new BurnState();
            state.Operation = "image";
            state.Steps = new string[] { "stage", "write" };
            state.DriveText = "none · writing to file";
            state.MediaText = "ISO image";
            state.SpeedText = "disk speed";

            MediaSnapshot fake = new MediaSnapshot();
            fake.Type = MediaType.DvdPlusR;

            Out.Title("build image");
            Out.Field("output", outPath);
            Out.Field("sources", Describe(sources));
            Out.Blank();

            StagedImage staged = null;
            Dashboard dash = new Dashboard(state, Program.ActiveTheme, Program.Fancy);
            try
            {
                dash.Start();
                staged = Burn.BuildImage(null, null, fake, sources, o.Label, false, o.FileSystems, state);

                state.Total = staged.TotalBytes;
                state.Step("write");
                state.Detail = "writing " + Path.GetFileName(outPath);
                state.Mode = DiscMode.Writing;

                using (ComStreamReader src = new ComStreamReader(staged.Stream))
                using (FileStream dst = File.Create(outPath))
                {
                    src.Seek(0, SeekOrigin.Begin);
                    byte[] buf = new byte[1 << 20];
                    long done = 0;
                    while (done < staged.TotalBytes)
                    {
                        int want = (int)Math.Min(buf.Length, staged.TotalBytes - done);
                        int got = src.Read(buf, 0, want);
                        if (got <= 0) break;
                        dst.Write(buf, 0, got);
                        done += got;
                        state.SetProgress(done, staged.TotalBytes);
                        if (Program.Cancelled) throw new BurnitException("Cancelled.");
                    }
                }
                state.Mode = DiscMode.Done;
                state.Detail = "complete";
                Thread.Sleep(250);
            }
            finally
            {
                dash.Stop();
                if (staged != null) staged.Dispose();
            }

            Out.Blank();
            Out.Good("wrote " + Fmt.Bytes(staged != null ? staged.TotalBytes : 0) + " to " + outPath);
            if (staged != null)
                Out.Note("  " + staged.FileCount + " files, " + staged.DirectoryCount
                    + " directories, volume " + staged.VolumeName);
            Out.Blank();
            return 0;
        }

        // ---- burn data ------------------------------------------------------------

        public static int BurnData(Options o)
        {
            if (o.Inputs.Count == 0)
                throw new BurnitException("Nothing to burn. Usage: burnit burn <file-or-folder>...");

            RecorderInfo info = Devices.Select(o.Drive);
            dynamic rec = Devices.OpenRecorder(info);
            dynamic data = null;
            StagedImage staged = null;

            try
            {
                data = Com.Create("IMAPI2.MsftDiscFormat2Data");
                data.Recorder = rec;
                data.ClientName = Program.ClientName;

                MediaSnapshot media = Devices.Inspect(rec);
                RequireWritableMedia(media, o);

                bool supported;
                try { supported = (bool)data.IsCurrentMediaSupported(rec); }
                catch (Exception) { supported = false; }
                if (!supported && !o.DryRun)
                    throw new BurnitException("This drive cannot write the disc that is loaded ("
                        + Media.Describe(media.Type) + ").");

                int speed = Devices.ResolveSpeed(o.Speed, media);

                BurnState state = new BurnState();
                state.Operation = "burn";
                state.DryRun = o.DryRun;
                state.Steps = new string[] { "stage", "validate", "calibrate", "write", "finalise" };
                state.DriveText = (info.Letter.Length > 0 ? info.Letter + "  " : "") + info.Name;
                state.MediaText = Media.Describe(media.Type) + " · " + Fmt.Bytes(media.FreeBytes) + " free";
                state.SpeedText = Devices.SpeedText(speed > 0 ? speed : media.CurrentSpeed, media.Type);

                // Stage first so the size is known before we ask for confirmation.
                Out.Title(o.DryRun ? "burn (dry run)" : "burn");
                Out.Field("drive", state.DriveText);
                Out.Field("media", Media.Describe(media.Type) + " · " + media.StateText);
                Out.Field("sources", Describe(o.Inputs));
                Out.Note("  staging...");

                Dashboard stageDash = new Dashboard(state, Program.ActiveTheme, Program.Fancy);
                try
                {
                    stageDash.Start();
                    staged = Burn.BuildImage(rec, data, media, o.Inputs, o.Label, o.Append, o.FileSystems, state);
                }
                finally { stageDash.Stop(); }

                long need = staged.TotalBytes;
                if (staged.TotalBlocks > media.FreeSectors && media.FreeSectors > 0)
                    throw new BurnitException("Image needs " + Fmt.Bytes(need) + " but only "
                        + Fmt.Bytes(media.FreeBytes) + " is free on this disc.");

                Out.Field("image", Fmt.Bytes(need) + " · " + staged.FileCount + " files, "
                    + staged.DirectoryCount + " dirs");
                Out.Field("volume", staged.VolumeName);
                Out.Field("speed", state.SpeedText);
                Out.Field("session", o.Close ? "close disc (no more writing)" : "leave disc open for more sessions");

                // A data disc full of audio files is a disc no CD player will play.
                // Cheap to warn, and the disc is write-once.
                if (IsAllAudio(o.Inputs))
                {
                    Out.Blank();
                    Out.Warn("every file here is an audio file, and this is a DATA disc.");
                    Out.Note("  A data disc will not play in a car stereo or a normal CD player —");
                    Out.Note("  they read Red Book audio CDs only. For a disc that plays, use:");
                    Out.Note("      burnit audio <files>");
                    Out.Note("  Carry on only if you meant to store the files as data.");
                }

                byte[] expected = null;
                if (o.Verify && !o.DryRun)
                {
                    if (info.Letter.Length == 0)
                        throw new BurnitException("--verify needs the drive to have a letter assigned.");
                    Out.Note("  hashing image for verification...");
                    BurnState hs = new BurnState();
                    hs.Operation = "hash image";
                    hs.Steps = new string[] { "hash" };
                    hs.Step("hash");
                    hs.Detail = "hashing staged image";
                    Dashboard hd = new Dashboard(hs, Program.ActiveTheme, Program.Fancy);
                    try
                    {
                        hd.Start();
                        using (ComStreamReader r = new ComStreamReader(staged.Stream))
                        {
                            r.Seek(0, SeekOrigin.Begin);
                            expected = Burn.HashStream(r, need, hs, DiscMode.Reading);
                        }
                    }
                    finally { hd.Stop(); }
                }

                if (!o.DryRun && !Out.Confirm("Write " + Fmt.Bytes(need) + " to the disc in "
                        + (info.Letter.Length > 0 ? info.Letter : info.Name) + "?", o.Yes))
                {
                    Out.Note("cancelled");
                    return 1;
                }

                // Rewind whatever the hash pass consumed.
                staged.Stream.Seek(0, 0, IntPtr.Zero);

                state.Done = 0;
                state.Total = need;
                state.StepIndex = 0;
                state.ReportedElapsed = -1;
                state.ReportedRemaining = -1;

                DataWriteSink sink = new DataWriteSink(state);
                Program.CancelHook = delegate { sink.CancelRequested = true; };

                Dashboard dash = new Dashboard(state, Program.ActiveTheme, Program.Fancy);
                DateTime t0 = DateTime.UtcNow;
                try
                {
                    dash.Start();
                    if (o.DryRun)
                    {
                        // Advise for real even though nothing is written: if the sink
                        // cannot attach, a real burn would run blind, and the dry run
                        // should be the thing that tells you.
                        using (new SinkConnection((object)data, SinkIid.DataWrite, sink))
                            Burn.Simulate(state, need, speed, Burn.SectorBytes, delegate { return Program.Cancelled; });
                    }
                    else
                        Burn.Write(data, staged.Stream, speed, o.Close, state, sink);
                    Thread.Sleep(250);
                }
                finally
                {
                    dash.Stop();
                    Program.CancelHook = null;
                }

                TimeSpan took = DateTime.UtcNow - t0;
                Out.Blank();
                if (o.DryRun)
                {
                    Out.Good("dry run complete — nothing was written to the disc");
                    Out.Note("  would have written " + Fmt.Bytes(need) + " at " + state.SpeedText);
                    Out.Blank();
                    return 0;
                }

                Out.Good("burned " + Fmt.Bytes(need) + " in " + Fmt.Time((int)took.TotalSeconds)
                    + "  (avg " + Fmt.Rate(need / Math.Max(1.0, took.TotalSeconds)) + ")");

                int rc = 0;
                if (o.Verify)
                    rc = VerifyBurn(info, need, expected);

                if (o.Eject)
                {
                    try { rec.EjectMedia(); Out.Good("ejected"); }
                    catch (Exception) { }
                }
                Out.Blank();
                return rc;
            }
            finally
            {
                if (staged != null) staged.Dispose();
                Com.Release(data);
                Com.Release(rec);
            }
        }

        // ---- burn an ISO ----------------------------------------------------------

        public static int BurnIso(Options o)
        {
            if (o.Inputs.Count != 1)
                throw new BurnitException("Usage: burnit iso <image.iso>");

            string isoPath = Path.GetFullPath(o.Inputs[0]);
            if (!File.Exists(isoPath))
                throw new BurnitException("Not found: " + isoPath);

            long isoBytes = new FileInfo(isoPath).Length;
            if (isoBytes == 0) throw new BurnitException("That image file is empty.");

            RecorderInfo info = Devices.Select(o.Drive);
            dynamic rec = Devices.OpenRecorder(info);
            dynamic data = null;
            dynamic isoMgr = null;
            STREAM stream = null;

            try
            {
                data = Com.Create("IMAPI2.MsftDiscFormat2Data");
                data.Recorder = rec;
                data.ClientName = Program.ClientName;

                MediaSnapshot media = Devices.Inspect(rec);
                RequireWritableMedia(media, o);

                long needSectors = (isoBytes + Burn.SectorBytes - 1) / Burn.SectorBytes;
                if (media.FreeSectors > 0 && needSectors > media.FreeSectors)
                    throw new BurnitException("That image needs " + Fmt.Bytes(isoBytes) + " but only "
                        + Fmt.Bytes(media.FreeBytes) + " is free.");

                // IsoImageManager sanity-checks the image; fall back to a plain file stream.
                string validation = null;
                try
                {
                    isoMgr = Com.Create("IMAPI2FS.MsftIsoImageManager");
                    isoMgr.SetPath(isoPath);
                    isoMgr.Validate();
                    stream = (STREAM)isoMgr.Stream;
                }
                catch (Exception e)
                {
                    validation = Errors.Describe(e);
                    Com.Release(isoMgr); isoMgr = null;
                    stream = Native.OpenFileStream(isoPath);
                }

                int speed = Devices.ResolveSpeed(o.Speed, media);

                BurnState state = new BurnState();
                state.Operation = "burn iso";
                state.DryRun = o.DryRun;
                state.Steps = new string[] { "validate", "calibrate", "write", "finalise" };
                state.DriveText = (info.Letter.Length > 0 ? info.Letter + "  " : "") + info.Name;
                state.MediaText = Media.Describe(media.Type) + " · " + Fmt.Bytes(media.FreeBytes) + " free";
                state.SpeedText = Devices.SpeedText(speed > 0 ? speed : media.CurrentSpeed, media.Type);
                state.Total = isoBytes;

                Out.Title(o.DryRun ? "burn iso (dry run)" : "burn iso");
                Out.Field("image", Path.GetFileName(isoPath) + " · " + Fmt.Bytes(isoBytes));
                Out.Field("drive", state.DriveText);
                Out.Field("media", Media.Describe(media.Type) + " · " + media.StateText);
                Out.Field("speed", state.SpeedText);
                if (validation != null)
                    Out.Warn("image did not pass ISO validation (" + validation + "); writing it raw");

                byte[] expected = null;
                if (o.Verify && !o.DryRun)
                {
                    if (info.Letter.Length == 0)
                        throw new BurnitException("--verify needs the drive to have a letter assigned.");
                    BurnState hs = new BurnState();
                    hs.Operation = "hash image";
                    hs.Steps = new string[] { "hash" };
                    hs.Step("hash");
                    hs.Detail = "hashing " + Path.GetFileName(isoPath);
                    Dashboard hd = new Dashboard(hs, Program.ActiveTheme, Program.Fancy);
                    try
                    {
                        hd.Start();
                        using (FileStream fs = File.OpenRead(isoPath))
                            expected = Burn.HashStream(fs, isoBytes, hs, DiscMode.Reading);
                    }
                    finally { hd.Stop(); }
                }

                if (!o.DryRun && !Out.Confirm("Write " + Path.GetFileName(isoPath) + " to the disc in "
                        + (info.Letter.Length > 0 ? info.Letter : info.Name) + "?", o.Yes))
                {
                    Out.Note("cancelled");
                    return 1;
                }

                try { stream.Seek(0, 0, IntPtr.Zero); }
                catch (Exception) { }

                DataWriteSink sink = new DataWriteSink(state);
                Program.CancelHook = delegate { sink.CancelRequested = true; };

                Dashboard dash = new Dashboard(state, Program.ActiveTheme, Program.Fancy);
                DateTime t0 = DateTime.UtcNow;
                try
                {
                    dash.Start();
                    if (o.DryRun)
                    {
                        using (new SinkConnection((object)data, SinkIid.DataWrite, sink))
                            Burn.Simulate(state, isoBytes, speed, Burn.SectorBytes, delegate { return Program.Cancelled; });
                    }
                    else
                        Burn.Write(data, stream, speed, o.Close, state, sink);
                    Thread.Sleep(250);
                }
                finally
                {
                    dash.Stop();
                    Program.CancelHook = null;
                }

                TimeSpan took = DateTime.UtcNow - t0;
                Out.Blank();
                if (o.DryRun)
                {
                    Out.Good("dry run complete — nothing was written to the disc");
                    Out.Blank();
                    return 0;
                }

                Out.Good("burned " + Fmt.Bytes(isoBytes) + " in " + Fmt.Time((int)took.TotalSeconds)
                    + "  (avg " + Fmt.Rate(isoBytes / Math.Max(1.0, took.TotalSeconds)) + ")");

                int rc = 0;
                if (o.Verify) rc = VerifyBurn(info, isoBytes, expected);

                if (o.Eject)
                {
                    try { rec.EjectMedia(); Out.Good("ejected"); }
                    catch (Exception) { }
                }
                Out.Blank();
                return rc;
            }
            finally
            {
                Com.Release(stream);
                Com.Release(isoMgr);
                Com.Release(data);
                Com.Release(rec);
            }
        }

        // ---- audio CD -------------------------------------------------------------

        public static int BurnAudio(Options o)
        {
            if (o.Inputs.Count == 0)
                throw new BurnitException("Usage: burnit audio <track.mp3|flac|wav>...");

            RecorderInfo info = Devices.Select(o.Drive);
            dynamic rec = Devices.OpenRecorder(info);
            dynamic tao = null;
            string tempDir = Path.Combine(Path.GetTempPath(), "burnit-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            try
            {
                Directory.CreateDirectory(tempDir);

                tao = Com.Create("IMAPI2.MsftDiscFormat2TrackAtOnce");
                tao.Recorder = rec;
                tao.ClientName = Program.ClientName;

                MediaSnapshot media = Devices.Inspect(rec);
                if (!Media.IsCd(media.Type))
                    throw new BurnitException("Audio CDs need CD-R or CD-RW media; this drive has "
                        + Media.Describe(media.Type) + " loaded.");
                RequireWritableMedia(media, o);

                BurnState state = new BurnState();
                state.Operation = "audio cd";
                state.DryRun = o.DryRun;
                state.Steps = new string[] { "decode", "prepare", "write", "finalise" };
                state.DriveText = (info.Letter.Length > 0 ? info.Letter + "  " : "") + info.Name;
                state.MediaText = Media.Describe(media.Type);
                state.Step("decode");

                Out.Title(o.DryRun ? "audio cd (dry run)" : "audio cd");
                Out.Field("drive", state.DriveText);
                Out.Field("media", Media.Describe(media.Type) + " · " + media.StateText);
                if (Audio.FindFfmpeg() == null)
                    Out.Warn("ffmpeg not on PATH — only 44.1 kHz 16-bit stereo WAV can be read");
                List<string> sources = ExpandAudioInputs(o.Inputs);
                Out.Note("  decoding " + sources.Count + " track" + (sources.Count == 1 ? "" : "s") + "...");

                List<AudioTrack> tracks = new List<AudioTrack>();
                Dashboard prep = new Dashboard(state, Program.ActiveTheme, Program.Fancy);
                try
                {
                    prep.Start();
                    for (int i = 0; i < sources.Count; i++)
                    {
                        state.SetProgress(i, sources.Count);
                        tracks.Add(Audio.Prepare(sources[i], tempDir, i + 1, state));
                        if (Program.Cancelled) throw new BurnitException("Cancelled.");
                    }
                    state.SetProgress(sources.Count, sources.Count);
                }
                finally { prep.Stop(); }

                int totalSectors = Audio.TotalSectors(tracks);
                int capacity = media.TotalSectors > 0 ? media.TotalSectors : 359849;
                Out.Blank();
                for (int i = 0; i < tracks.Count; i++)
                    Console.WriteLine("  " + (i + 1).ToString("00", CultureInfo.InvariantCulture) + "  "
                        + Fmt.Ellipsis(tracks[i].Title, 44).PadRight(46) + tracks[i].Duration);
                Out.Blank();
                Out.Field("total", Audio.TotalDuration(tracks) + " over " + tracks.Count + " tracks");
                Out.Field("disc", Math.Round(capacity / 4500.0) + " minute CD · "
                    + (100.0 * totalSectors / capacity).ToString("0.0", CultureInfo.InvariantCulture) + "% used");

                if (totalSectors > capacity)
                    throw new BurnitException("Those tracks need " + (totalSectors / 75 / 60) + ":"
                        + ((totalSectors / 75) % 60).ToString("00", CultureInfo.InvariantCulture)
                        + " but the disc holds " + (capacity / 75 / 60) + " minutes.");

                int speed = Devices.ResolveSpeed(o.Speed, media);
                state.SpeedText = Devices.SpeedText(speed > 0 ? speed : media.CurrentSpeed, media.Type);
                Out.Field("speed", state.SpeedText);
                Out.Field("session", o.NoFinalize ? "leave open (not playable until finalised)" : "finalise");

                if (!o.DryRun && !Out.Confirm("Write " + tracks.Count + " tracks to the disc in "
                        + (info.Letter.Length > 0 ? info.Letter : info.Name) + "?", o.Yes))
                {
                    Out.Note("cancelled");
                    return 1;
                }

                TrackAtOnceSink sink = new TrackAtOnceSink(state);
                sink.SectorTotal = totalSectors;
                Program.CancelHook = delegate { sink.CancelRequested = true; };

                state.Done = 0;
                state.Total = (long)totalSectors * Audio.SectorBytes;
                state.StepIndex = 1;

                Dashboard dash = new Dashboard(state, Program.ActiveTheme, Program.Fancy);
                DateTime t0 = DateTime.UtcNow;
                try
                {
                    dash.Start();
                    if (o.DryRun)
                    {
                        using (new SinkConnection((object)tao, SinkIid.TrackAtOnce, sink))
                            Burn.Simulate(state, state.Total, speed, Audio.SectorBytes, delegate { return Program.Cancelled; });
                    }
                    else
                    {
                        if (speed > 0)
                        {
                            try { tao.SetWriteSpeed(speed, false); }
                            catch (Exception) { }
                        }
                        try { tao.DoNotFinalizeMedia = o.NoFinalize; }
                        catch (Exception) { }

                        using (new SinkConnection((object)tao, SinkIid.TrackAtOnce, sink))
                        {
                            state.Step("prepare");
                            state.Detail = "preparing disc";
                            tao.PrepareMedia();
                            try
                            {
                                long baseSectors = 0;
                                for (int i = 0; i < tracks.Count; i++)
                                {
                                    sink.SectorBase = baseSectors;
                                    state.Detail = "writing track " + (i + 1) + "  " + tracks[i].Title;
                                    STREAM ts = Native.OpenFileStream(tracks[i].RawPath);
                                    try { tao.AddAudioTrack(ts); }
                                    finally { Com.Release(ts); }
                                    baseSectors += tracks[i].Sectors + Audio.GapSectors;
                                }
                            }
                            finally
                            {
                                state.Step("finalise");
                                state.Detail = "finalising disc";
                                tao.ReleaseMedia();
                            }
                        }
                        state.Mode = DiscMode.Done;
                        state.SetProgress(state.Total, state.Total);
                    }
                    Thread.Sleep(250);
                }
                finally
                {
                    dash.Stop();
                    Program.CancelHook = null;
                }

                TimeSpan took = DateTime.UtcNow - t0;
                Out.Blank();
                if (o.DryRun)
                {
                    Out.Good("dry run complete — nothing was written to the disc");
                    Out.Blank();
                    return 0;
                }
                Out.Good("wrote " + tracks.Count + " tracks (" + Audio.TotalDuration(tracks) + ") in "
                    + Fmt.Time((int)took.TotalSeconds));
                if (o.NoFinalize)
                    Out.Warn("disc is not finalised; most CD players will not read it yet");
                if (o.Eject)
                {
                    try { rec.EjectMedia(); Out.Good("ejected"); }
                    catch (Exception) { }
                }
                Out.Blank();
                return 0;
            }
            finally
            {
                Com.Release(tao);
                Com.Release(rec);
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch (Exception) { }
            }
        }

        // ---- erase ----------------------------------------------------------------

        public static int Erase(Options o)
        {
            RecorderInfo info = Devices.Select(o.Drive);
            dynamic rec = Devices.OpenRecorder(info);
            dynamic eraser = null;
            try
            {
                MediaSnapshot media = Devices.Inspect(rec);
                if (!media.Present)
                    throw new BurnitException("No disc in the drive.");
                if (!Media.IsRewritable(media.Type))
                    throw new BurnitException(Media.Describe(media.Type) + " cannot be erased; it is write-once media.");

                eraser = Com.Create("IMAPI2.MsftDiscFormat2Erase");
                eraser.Recorder = rec;
                eraser.ClientName = Program.ClientName;
                eraser.FullErase = o.Full;

                bool ok;
                try { ok = (bool)eraser.IsCurrentMediaSupported(rec); }
                catch (Exception) { ok = false; }
                if (!ok) throw new BurnitException("This drive will not erase the loaded disc.");

                Out.Title("erase");
                Out.Field("drive", (info.Letter.Length > 0 ? info.Letter + "  " : "") + info.Name);
                Out.Field("media", Media.Describe(media.Type) + " · " + media.StateText);
                Out.Field("mode", o.Full ? "full erase (slow, overwrites everything)" : "quick erase");

                if (!Out.Confirm("Erase the disc in " + (info.Letter.Length > 0 ? info.Letter : info.Name)
                        + "? This cannot be undone.", o.Yes))
                {
                    Out.Note("cancelled");
                    return 1;
                }

                BurnState state = new BurnState();
                state.Operation = "erase";
                state.Steps = new string[] { "erase" };
                state.Step("erase");
                state.DriveText = (info.Letter.Length > 0 ? info.Letter + "  " : "") + info.Name;
                state.MediaText = Media.Describe(media.Type);
                state.SpeedText = o.Full ? "full" : "quick";
                state.Mode = DiscMode.Erasing;
                state.Detail = "erasing";
                state.Total = 1;

                EraseSink sink = new EraseSink(state);
                Dashboard dash = new Dashboard(state, Program.ActiveTheme, Program.Fancy);
                DateTime t0 = DateTime.UtcNow;
                try
                {
                    dash.Start();
                    using (new SinkConnection((object)eraser, SinkIid.Erase, sink))
                    {
                        eraser.EraseMedia();
                    }
                    state.SetProgress(state.Total, state.Total);
                    state.Mode = DiscMode.Idle;
                    Thread.Sleep(250);
                }
                finally { dash.Stop(); }

                Out.Blank();
                Out.Good("erased in " + Fmt.Time((int)(DateTime.UtcNow - t0).TotalSeconds));
                Out.Blank();
                return 0;
            }
            finally
            {
                Com.Release(eraser);
                Com.Release(rec);
            }
        }

        // ---- helpers --------------------------------------------------------------

        private static int VerifyBurn(RecorderInfo info, long bytes, byte[] expected)
        {
            if (expected == null) return 0;

            BurnState vs = new BurnState();
            vs.Operation = "verify";
            vs.Steps = new string[] { "read back", "compare" };
            vs.Step("read back");
            vs.DriveText = (info.Letter.Length > 0 ? info.Letter + "  " : "") + info.Name;
            vs.MediaText = "reading disc";
            vs.SpeedText = "read";
            vs.Total = bytes;
            vs.Mode = DiscMode.Reading;
            vs.Detail = "reading the disc back";

            byte[] actual;
            Dashboard dash = new Dashboard(vs, Program.ActiveTheme, Program.Fancy);
            try
            {
                dash.Start();
                actual = Burn.HashDisc(info.Letter, bytes, vs);
                vs.Step("compare");
            }
            finally { dash.Stop(); }

            if (Burn.SameHash(expected, actual))
            {
                Out.Good("verified — sha256 " + Burn.Hex(expected).Substring(0, 16) + "… matches");
                return 0;
            }

            Out.Bad("VERIFY FAILED — the disc does not match the image");
            Out.Note("  expected " + Burn.Hex(expected));
            Out.Note("  on disc  " + Burn.Hex(actual));
            return 2;
        }

        private static void RequireWritableMedia(MediaSnapshot media, Options o)
        {
            if (!media.Present)
                throw new BurnitException(media.Problem ?? "No disc in the drive.");
            if ((media.State & MediaState.WriteProtected) != 0)
                throw new BurnitException("The disc is write-protected.");
            if ((media.State & MediaState.Damaged) != 0)
                throw new BurnitException("The drive reports this disc as damaged.");
            if ((media.State & MediaState.EraseRequired) != 0)
                throw new BurnitException("This disc must be erased first. Run: burnit erase");
            if ((media.State & MediaState.Finalized) != 0 && !o.DryRun)
                throw new BurnitException("This disc is closed; nothing more can be written to it.");

            bool blank = media.PhysicallyBlank || (media.State & MediaState.Blank) != 0;
            bool appendable = (media.State & MediaState.Appendable) != 0;
            if (!blank && !appendable && !o.DryRun)
                throw new BurnitException("This disc is neither blank nor appendable. Erase it, or use a fresh disc.");
            if (!blank && !o.Append && !o.DryRun)
                Out.Warn("disc already has data; a new session will be appended");
        }

        /// <summary>
        /// Turns whatever was on the command line into a track list: a folder becomes
        /// its audio files in name order, a file stays as it is. Track order is the
        /// order given, so pass files individually to control the running order.
        /// </summary>
        private static List<string> ExpandAudioInputs(IList<string> inputs)
        {
            List<string> outList = new List<string>();
            foreach (string raw in inputs)
            {
                string p;
                try { p = Path.GetFullPath(raw); }
                catch (Exception) { throw new BurnitException("Not a usable path: " + raw); }

                if (Directory.Exists(p))
                {
                    List<string> found = new List<string>();
                    foreach (string f in Directory.GetFiles(p, "*", SearchOption.TopDirectoryOnly))
                        if (Array.IndexOf(AudioExtensions, Path.GetExtension(f).ToLowerInvariant()) >= 0)
                            found.Add(f);
                    if (found.Count == 0)
                        throw new BurnitException("No audio files in " + p);
                    found.Sort(StringComparer.OrdinalIgnoreCase);
                    outList.AddRange(found);
                }
                else if (File.Exists(p))
                {
                    outList.Add(p);
                }
                else
                {
                    throw new BurnitException("Not found: " + raw);
                }
            }
            if (outList.Count == 0)
                throw new BurnitException("No audio files to burn.");
            if (outList.Count > 99)
                throw new BurnitException("An audio CD holds at most 99 tracks; you gave " + outList.Count + ".");
            return outList;
        }

        private static readonly string[] AudioExtensions =
        {
            ".flac", ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".oga", ".opus",
            ".wma", ".alac", ".aif", ".aiff", ".ape", ".wv", ".mpc"
        };

        /// <summary>True when every file being burned is an audio file and there is at least one.</summary>
        private static bool IsAllAudio(IList<string> inputs)
        {
            int seen = 0;
            foreach (string s in inputs)
            {
                string[] files;
                try
                {
                    string p = Path.GetFullPath(s);
                    if (Directory.Exists(p)) files = Directory.GetFiles(p, "*", SearchOption.AllDirectories);
                    else if (File.Exists(p)) files = new string[] { p };
                    else continue;
                }
                catch (Exception) { continue; }

                foreach (string f in files)
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (Array.IndexOf(AudioExtensions, ext) < 0) return false;
                    seen++;
                    if (seen > 5000) return true;      // don't walk a huge tree forever
                }
            }
            return seen > 0;
        }

        private static string Describe(IList<string> inputs)
        {
            int files = 0, dirs = 0;
            long bytes = 0;
            foreach (string s in inputs)
            {
                try
                {
                    string p = Path.GetFullPath(s);
                    if (Directory.Exists(p))
                    {
                        dirs++;
                        foreach (string f in Directory.GetFiles(p, "*", SearchOption.AllDirectories))
                        {
                            files++;
                            bytes += new FileInfo(f).Length;
                        }
                    }
                    else if (File.Exists(p))
                    {
                        files++;
                        bytes += new FileInfo(p).Length;
                    }
                }
                catch (Exception) { }
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(files).Append(files == 1 ? " file" : " files");
            if (dirs > 0) sb.Append(", ").Append(dirs).Append(dirs == 1 ? " folder" : " folders");
            sb.Append(" · ").Append(Fmt.Bytes(bytes));
            return sb.ToString();
        }
    }
}
