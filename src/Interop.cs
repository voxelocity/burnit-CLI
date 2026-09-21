// IMAPI2 interop.
//
// Everything that can be reached through IDispatch is reached through `dynamic`,
// so there are no hand-transcribed vtable layouts to get subtly wrong. The only
// hard-declared interfaces are the four event sinks, because those we have to
// *implement* rather than call. Their IIDs and DISPIDs were read out of the type
// libraries in imapi2.dll / imapi2fs.dll (FUNCDESC.memid), not guessed.

using System;
using System.Runtime.InteropServices;

namespace Burnit
{
    public static class Com
    {
        public static dynamic Create(string progId)
        {
            Type t = Type.GetTypeFromProgID(progId, false);
            if (t == null)
                throw new BurnitException("COM class " + progId + " is not registered; IMAPI2 is missing from this Windows install.");
            return Activator.CreateInstance(t);
        }

        public static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o))
            {
                try { Marshal.ReleaseComObject(o); }
                catch (Exception) { }
            }
        }
    }

    // ---- Connection-point plumbing ------------------------------------------------

    /// <summary>Advises an event sink on a COM source, and unadvises on Dispose.</summary>
    public sealed class SinkConnection : IDisposable
    {
        private System.Runtime.InteropServices.ComTypes.IConnectionPoint _cp;
        private int _cookie;

        public SinkConnection(object source, Guid sinkIid, object sink)
        {
            var container = source as System.Runtime.InteropServices.ComTypes.IConnectionPointContainer;
            if (container == null)
                throw new BurnitException("COM object does not expose IConnectionPointContainer.");
            Guid iid = sinkIid;
            container.FindConnectionPoint(ref iid, out _cp);
            _cp.Advise(sink, out _cookie);
        }

        public void Dispose()
        {
            if (_cp != null)
            {
                try { _cp.Unadvise(_cookie); }
                catch (Exception) { }
                Com.Release(_cp);
                _cp = null;
            }
        }
    }

    // ---- Event sink interfaces ----------------------------------------------------
    // Declared dual (the ComImport default) so the CCW answers both a vtable call and
    // IDispatch::Invoke; IMAPI2 is scriptable and may legitimately use either path.

    [ComImport]
    [Guid("2735413C-7F64-5B0F-8F00-5D77AFBE261E")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IDataWriteEvents
    {
        [DispId(0x200)]
        void Update(
            [In, MarshalAs(UnmanagedType.IDispatch)] object sender,
            [In, MarshalAs(UnmanagedType.IDispatch)] object progress);
    }

    [ComImport]
    [Guid("2735413F-7F64-5B0F-8F00-5D77AFBE261E")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface ITrackAtOnceEvents
    {
        [DispId(0x200)]
        void Update(
            [In, MarshalAs(UnmanagedType.IDispatch)] object sender,
            [In, MarshalAs(UnmanagedType.IDispatch)] object progress);
    }

    [ComImport]
    [Guid("2735413A-7F64-5B0F-8F00-5D77AFBE261E")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IEraseEvents
    {
        [DispId(0x200)]
        void Update(
            [In, MarshalAs(UnmanagedType.IDispatch)] object sender,
            [In] int elapsedSeconds,
            [In] int estimatedTotalSeconds);
    }

    [ComImport]
    [Guid("2C941FDF-975B-59BE-A960-9A2A262853A5")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IFsImageEvents
    {
        [DispId(0x100)]
        void Update(
            [In, MarshalAs(UnmanagedType.IDispatch)] object sender,
            [In, MarshalAs(UnmanagedType.BStr)] string currentFile,
            [In] int copiedSectors,
            [In] int totalSectors);
    }

    public static class SinkIid
    {
        public static readonly Guid DataWrite   = new Guid("2735413C-7F64-5B0F-8F00-5D77AFBE261E");
        public static readonly Guid TrackAtOnce = new Guid("2735413F-7F64-5B0F-8F00-5D77AFBE261E");
        public static readonly Guid Erase       = new Guid("2735413A-7F64-5B0F-8F00-5D77AFBE261E");
        public static readonly Guid FsImage     = new Guid("2C941FDF-975B-59BE-A960-9A2A262853A5");
    }

    // ---- IMAPI2 constants ---------------------------------------------------------

    public enum MediaType
    {
        Unknown = 0, CdRom = 1, CdR = 2, CdRw = 3, DvdRom = 4, DvdRam = 5,
        DvdPlusR = 6, DvdPlusRw = 7, DvdPlusRDual = 8, DvdDashR = 9, DvdDashRw = 10,
        DvdDashRDual = 11, Disk = 12, DvdPlusRwDual = 13, HdDvdRom = 14, HdDvdR = 15,
        HdDvdRam = 16, BdRom = 17, BdR = 18, BdRe = 19
    }

    [Flags]
    public enum MediaState
    {
        Unknown = 0,
        Overwrite = 0x1,
        Blank = 0x2,
        Appendable = 0x4,
        FinalSession = 0x8,
        Damaged = 0x400,
        EraseRequired = 0x800,
        NonEmptySession = 0x1000,
        WriteProtected = 0x2000,
        Finalized = 0x4000,
        UnsupportedMedia = 0x8000
    }

    public enum DataAction
    {
        ValidatingMedia = 0, FormattingMedia = 1, InitializingHardware = 2,
        CalibratingPower = 3, WritingData = 4, Finalization = 5, Completed = 6, Verifying = 7
    }

    public enum TaoAction
    {
        Unknown = 0, Preparing = 1, Writing = 2, Finishing = 3, Verifying = 4
    }

    [Flags]
    public enum FsiFileSystems
    {
        None = 0, Iso9660 = 1, Joliet = 2, Udf = 4, Unknown = 0x40000000
    }

    public static class Media
    {
        public static string Describe(MediaType t)
        {
            switch (t)
            {
                case MediaType.CdRom: return "CD-ROM";
                case MediaType.CdR: return "CD-R";
                case MediaType.CdRw: return "CD-RW";
                case MediaType.DvdRom: return "DVD-ROM";
                case MediaType.DvdRam: return "DVD-RAM";
                case MediaType.DvdPlusR: return "DVD+R";
                case MediaType.DvdPlusRw: return "DVD+RW";
                case MediaType.DvdPlusRDual: return "DVD+R DL";
                case MediaType.DvdDashR: return "DVD-R";
                case MediaType.DvdDashRw: return "DVD-RW";
                case MediaType.DvdDashRDual: return "DVD-R DL";
                case MediaType.Disk: return "Disk";
                case MediaType.DvdPlusRwDual: return "DVD+RW DL";
                case MediaType.HdDvdRom: return "HD DVD-ROM";
                case MediaType.HdDvdR: return "HD DVD-R";
                case MediaType.HdDvdRam: return "HD DVD-RAM";
                case MediaType.BdRom: return "BD-ROM";
                case MediaType.BdR: return "BD-R";
                case MediaType.BdRe: return "BD-RE";
                default: return "no disc";
            }
        }

        /// <summary>Sectors/second at 1x, for turning IMAPI speeds into the familiar "24x".</summary>
        public static int BaseSectorsPerSecond(MediaType t)
        {
            switch (t)
            {
                case MediaType.CdRom:
                case MediaType.CdR:
                case MediaType.CdRw:
                case MediaType.Unknown:
                    return 75;      // 150 KB/s
                case MediaType.BdRom:
                case MediaType.BdR:
                case MediaType.BdRe:
                    return 2195;    // 4.5 MB/s
                default:
                    return 676;     // DVD 1x = 1385 KB/s
            }
        }

        public static bool IsCd(MediaType t)
        {
            return t == MediaType.CdRom || t == MediaType.CdR || t == MediaType.CdRw;
        }

        public static bool IsRewritable(MediaType t)
        {
            return t == MediaType.CdRw || t == MediaType.DvdPlusRw || t == MediaType.DvdDashRw
                || t == MediaType.DvdRam || t == MediaType.DvdPlusRwDual || t == MediaType.BdRe
                || t == MediaType.HdDvdRam;
        }
    }

    public class BurnitException : Exception
    {
        public BurnitException(string message) : base(message) { }
        public BurnitException(string message, Exception inner) : base(message, inner) { }
    }
}
