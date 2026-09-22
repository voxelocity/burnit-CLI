// Drive enumeration and media inspection.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Burnit
{
    public sealed class RecorderInfo
    {
        public string Id;
        public string Vendor = "";
        public string Product = "";
        public string Revision = "";
        public string[] Paths = new string[0];
        public int Index;

        public string Letter
        {
            get
            {
                if (Paths.Length == 0) return "";
                string p = Paths[0];
                if (p.Length >= 2 && p[1] == ':') return p.Substring(0, 2);
                return "";
            }
        }

        public string Name
        {
            get
            {
                string n = (Vendor + " " + Product).Trim();
                while (n.Contains("  ")) n = n.Replace("  ", " ");
                return n.Length == 0 ? "optical drive" : n;
            }
        }

        public override string ToString()
        {
            string l = Letter;
            return (l.Length > 0 ? l + "  " : "") + Name;
        }
    }

    public sealed class MediaSnapshot
    {
        public MediaType Type = MediaType.Unknown;
        public MediaState State = MediaState.Unknown;
        public bool PhysicallyBlank;
        public int TotalSectors;
        public int FreeSectors;
        public int NextWritable = -1;
        public int[] SupportedSpeeds = new int[0];
        public int CurrentSpeed;
        public bool Present;
        public string Problem;

        /// <summary>A stand-in 80 minute CD-R, for planning a burn with no disc loaded.</summary>
        public static MediaSnapshot AssumedBlankCd()
        {
            MediaSnapshot m = new MediaSnapshot();
            m.Type = MediaType.CdR;
            m.State = MediaState.Blank | MediaState.Appendable;
            m.PhysicallyBlank = true;
            m.Present = true;
            m.TotalSectors = 359847;
            m.FreeSectors = 359847;
            m.SupportedSpeeds = new int[] { 750, 1199, 1799 };
            m.CurrentSpeed = 1799;
            return m;
        }

        public long CapacityBytes { get { return (long)TotalSectors * 2048L; } }
        public long FreeBytes { get { return (long)FreeSectors * 2048L; } }

        public string StateText
        {
            get
            {
                if (!Present) return "no disc";
                List<string> parts = new List<string>();
                if (PhysicallyBlank || (State & MediaState.Blank) != 0) parts.Add("blank");
                if ((State & MediaState.Appendable) != 0) parts.Add("appendable");
                if ((State & MediaState.Finalized) != 0) parts.Add("closed");
                if ((State & MediaState.Overwrite) != 0) parts.Add("overwritable");
                if ((State & MediaState.EraseRequired) != 0) parts.Add("needs erase");
                if ((State & MediaState.WriteProtected) != 0) parts.Add("write-protected");
                if ((State & MediaState.Damaged) != 0) parts.Add("damaged");
                if ((State & MediaState.UnsupportedMedia) != 0) parts.Add("unsupported");
                if (parts.Count == 0) parts.Add("in use");
                return string.Join(", ", parts.ToArray());
            }
        }
    }

    public static class Devices
    {
        public static List<RecorderInfo> Enumerate()
        {
            List<RecorderInfo> list = new List<RecorderInfo>();
            dynamic master = null;
            try
            {
                master = Com.Create("IMAPI2.MsftDiscMaster2");
                bool supported;
                try { supported = (bool)master.IsSupportedEnvironment; }
                catch (Exception) { supported = true; }
                if (!supported) return list;

                int count = (int)master.Count;
                for (int i = 0; i < count; i++)
                {
                    string id = (string)master.Item(i);
                    RecorderInfo info = new RecorderInfo();
                    info.Id = id;
                    info.Index = i;

                    dynamic rec = null;
                    try
                    {
                        rec = Com.Create("IMAPI2.MsftDiscRecorder2");
                        rec.InitializeDiscRecorder(id);
                        try { info.Vendor = ((string)rec.VendorId ?? "").Trim(); }
                        catch (Exception) { }
                        try { info.Product = ((string)rec.ProductId ?? "").Trim(); }
                        catch (Exception) { }
                        try { info.Revision = ((string)rec.ProductRevision ?? "").Trim(); }
                        catch (Exception) { }
                        try
                        {
                            object[] paths = (object[])rec.VolumePathNames;
                            List<string> ps = new List<string>();
                            if (paths != null)
                                foreach (object o in paths)
                                    if (o != null) ps.Add(o.ToString());
                            info.Paths = ps.ToArray();
                        }
                        catch (Exception) { }
                    }
                    finally { Com.Release(rec); }

                    list.Add(info);
                }
            }
            finally { Com.Release(master); }
            return list;
        }

        /// <summary>Picks a recorder by drive letter or index; falls back to the only one present.</summary>
        public static RecorderInfo Select(string wanted)
        {
            List<RecorderInfo> all = Enumerate();
            if (all.Count == 0)
                throw new BurnitException("No optical recorders found. Is a burner attached?");

            if (string.IsNullOrEmpty(wanted))
            {
                if (all.Count == 1) return all[0];
                // Prefer the first with a drive letter.
                foreach (RecorderInfo r in all)
                    if (r.Letter.Length > 0) return r;
                return all[0];
            }

            string w = wanted.Trim().TrimEnd('\\');
            int idx;
            if (int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
            {
                if (idx >= 0 && idx < all.Count) return all[idx];
                throw new BurnitException("No drive with index " + idx + ".");
            }

            if (w.Length >= 1 && char.IsLetter(w[0]))
            {
                string letter = w.Substring(0, 1).ToUpperInvariant() + ":";
                foreach (RecorderInfo r in all)
                    if (string.Equals(r.Letter, letter, StringComparison.OrdinalIgnoreCase)) return r;
                throw new BurnitException("No optical recorder on drive " + letter);
            }

            throw new BurnitException("Could not interpret drive '" + wanted + "'.");
        }

        public static dynamic OpenRecorder(RecorderInfo info)
        {
            dynamic rec = Com.Create("IMAPI2.MsftDiscRecorder2");
            rec.InitializeDiscRecorder(info.Id);
            return rec;
        }

        /// <summary>Reads what the drive currently reports about the loaded disc.</summary>
        public static MediaSnapshot Inspect(dynamic recorder)
        {
            MediaSnapshot m = new MediaSnapshot();
            dynamic data = null;
            try
            {
                data = Com.Create("IMAPI2.MsftDiscFormat2Data");
                data.Recorder = recorder;
                data.ClientName = Program.ClientName;

                try { m.Type = (MediaType)(int)data.CurrentPhysicalMediaType; }
                catch (Exception) { }

                try
                {
                    m.State = (MediaState)(int)data.CurrentMediaStatus;
                    m.Present = true;
                }
                catch (Exception e) { m.Problem = Errors.Describe(e); }

                try { m.PhysicallyBlank = (bool)data.MediaPhysicallyBlank; }
                catch (Exception) { }
                try { m.TotalSectors = (int)data.TotalSectorsOnMedia; }
                catch (Exception) { }
                try { m.FreeSectors = (int)data.FreeSectorsOnMedia; }
                catch (Exception) { }
                try { m.NextWritable = (int)data.NextWritableAddress; }
                catch (Exception) { }
                try { m.CurrentSpeed = (int)data.CurrentWriteSpeed; }
                catch (Exception) { }
                try
                {
                    object[] speeds = (object[])data.SupportedWriteSpeeds;
                    List<int> s = new List<int>();
                    if (speeds != null)
                        foreach (object o in speeds)
                            s.Add(Convert.ToInt32(o, CultureInfo.InvariantCulture));
                    s.Sort();
                    m.SupportedSpeeds = s.ToArray();
                }
                catch (Exception) { }

                if (m.Type == MediaType.Unknown && m.TotalSectors == 0) m.Present = false;
            }
            catch (Exception e)
            {
                m.Problem = Errors.Describe(e);
            }
            finally { Com.Release(data); }
            return m;
        }

        /// <summary>Turns "24", "24x" or "max" into a supported sectors-per-second value.</summary>
        public static int ResolveSpeed(string spec, MediaSnapshot media)
        {
            if (media.SupportedSpeeds.Length == 0) return 0;
            int max = media.SupportedSpeeds[media.SupportedSpeeds.Length - 1];
            int min = media.SupportedSpeeds[0];

            if (string.IsNullOrEmpty(spec)) return 0;             // 0 => leave the drive's default
            string s = spec.Trim().ToLowerInvariant();
            if (s == "max") return max;
            if (s == "min") return min;
            s = s.TrimEnd('x');                                  // after max/min, or "max" loses its x

            double mult;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out mult) || mult <= 0)
                throw new BurnitException("Speed must be a number like 8, 24x, min or max.");

            int wantSectors = (int)Math.Round(mult * Media.BaseSectorsPerSecond(media.Type));
            int best = media.SupportedSpeeds[0];
            int bestDiff = int.MaxValue;
            foreach (int sp in media.SupportedSpeeds)
            {
                int d = Math.Abs(sp - wantSectors);
                if (d < bestDiff) { bestDiff = d; best = sp; }
            }
            return best;
        }

        public static string SpeedText(int sectorsPerSecond, MediaType type)
        {
            if (sectorsPerSecond <= 0) return "drive default";
            double x = (double)sectorsPerSecond / Media.BaseSectorsPerSecond(type);
            double mbs = sectorsPerSecond * 2048.0 / 1000000.0;
            return x.ToString(x >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture) + "x · "
                 + mbs.ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
        }
    }

    public static class Errors
    {
        /// <summary>Maps the IMAPI2 HRESULTs people actually hit onto plain sentences.</summary>
        public static string Describe(Exception e)
        {
            int hr = System.Runtime.InteropServices.Marshal.GetHRForException(e);
            switch ((uint)hr)
            {
                case 0xC0AA0202: return "No disc in the drive.";
                case 0xC0AA0203: return "The drive is in use by another program.";
                case 0xC0AA0204: return "The disc is write-protected.";
                case 0xC0AA0205: return "The media is not supported for this operation.";
                case 0xC0AA0207: return "The drive reported a media error.";
                case 0xC0AA0210: return "The disc is not blank; erase it or use --append.";
                case 0xC0AA0211: return "The data does not fit on this disc.";
                case 0xC0AA0301: return "Write failed: the drive reported a hardware error.";
                case 0xC0AA0402: return "The drive could not be locked for exclusive access.";
                case 0x80070020: return "The drive is locked by another process (Explorer may be indexing it).";
                case 0x8007001F: return "The device is not functioning.";
                case 0x80004005: return "Unspecified failure from the burning engine.";
            }
            string m = e.Message;
            if (string.IsNullOrEmpty(m)) m = "error 0x" + ((uint)hr).ToString("X8");
            return m.Trim();
        }
    }
}
