// Win32 bits: VT-enabling the console, wrapping a file as a COM IStream, and
// opening the optical drive for raw sector reads during verification.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;
using STREAM = System.Runtime.InteropServices.ComTypes.IStream;

namespace Burnit
{
    [SuppressUnmanagedCodeSecurity]
    internal static class Native
    {
        private const int STD_OUTPUT_HANDLE = -11;
        private const int ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
        private const int ENABLE_PROCESSED_OUTPUT = 0x0001;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr handle, out int mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr handle, int mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleScreenBufferInfo(IntPtr handle, out CONSOLE_SCREEN_BUFFER_INFO info);

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SMALL_RECT { public short Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CONSOLE_SCREEN_BUFFER_INFO
        {
            public COORD Size;
            public COORD CursorPosition;
            public short Attributes;
            public SMALL_RECT Window;
            public COORD MaximumWindowSize;
        }

        private static int _savedMode;
        private static IntPtr _outHandle = IntPtr.Zero;
        private static bool _modeSaved;

        /// <summary>Turns on ANSI escape handling. Returns false if the console cannot do it.</summary>
        public static bool EnableVirtualTerminal()
        {
            try
            {
                _outHandle = GetStdHandle(STD_OUTPUT_HANDLE);
                if (_outHandle == IntPtr.Zero || _outHandle == new IntPtr(-1)) return false;
                int mode;
                if (!GetConsoleMode(_outHandle, out mode)) return false;
                _savedMode = mode;
                _modeSaved = true;
                if ((mode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0) return true;
                return SetConsoleMode(_outHandle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING | ENABLE_PROCESSED_OUTPUT);
            }
            catch (Exception) { return false; }
        }

        public static void RestoreConsoleMode()
        {
            if (_modeSaved && _outHandle != IntPtr.Zero)
            {
                try { SetConsoleMode(_outHandle, _savedMode); }
                catch (Exception) { }
            }
        }

        /// <summary>Visible console size, falling back to 80x25 when there is no real console.</summary>
        public static void GetConsoleSize(out int width, out int height)
        {
            width = 80; height = 25;

            // Explicit override, for recording a session or driving an odd terminal.
            string forced = Environment.GetEnvironmentVariable("BURNIT_SIZE");
            if (!string.IsNullOrEmpty(forced))
            {
                string[] wh = forced.Split('x', 'X', ',');
                int fw, fh;
                if (wh.Length == 2
                    && int.TryParse(wh[0], out fw) && int.TryParse(wh[1], out fh)
                    && fw >= 20 && fh >= 10)
                {
                    width = fw; height = fh;
                    return;
                }
            }

            try
            {
                CONSOLE_SCREEN_BUFFER_INFO info;
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (GetConsoleScreenBufferInfo(h, out info))
                {
                    width = info.Window.Right - info.Window.Left + 1;
                    height = info.Window.Bottom - info.Window.Top + 1;
                }
                else
                {
                    // stdout is a pipe; ask the attached console directly.
                    using (SafeFileHandle con = CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE,
                               FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                    {
                        if (!con.IsInvalid && GetConsoleScreenBufferInfo(con.DangerousGetHandle(), out info))
                        {
                            width = info.Window.Right - info.Window.Left + 1;
                            height = info.Window.Bottom - info.Window.Top + 1;
                        }
                    }
                }
            }
            catch (Exception) { }
            if (width < 20) width = 80;
            if (height < 10) height = 25;
        }

        // ---- File as IStream ------------------------------------------------------
        // Lets IMAPI2 pull bytes straight from disk without a managed COM server in the
        // hot path, which keeps the write engine's own buffering intact.

        private const int STGM_READ = 0x00000000;
        private const int STGM_SHARE_DENY_WRITE = 0x00000020;

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateStreamOnFileEx(
            string pszFile, int grfMode, int dwAttributes, bool fCreate,
            IntPtr pstmTemplate,
            [MarshalAs(UnmanagedType.Interface)] out STREAM ppstm);

        public static STREAM OpenFileStream(string path)
        {
            STREAM s;
            SHCreateStreamOnFileEx(path, STGM_READ | STGM_SHARE_DENY_WRITE, 0, false, IntPtr.Zero, out s);
            return s;
        }

        // ---- Raw device access ----------------------------------------------------

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        /// <summary>
        /// Opens \\.\X: for raw sector reads. Used only to read a burn back for
        /// verification; never opened for write.
        /// </summary>
        public static FileStream OpenRawDevice(string driveLetter)
        {
            string letter = driveLetter.TrimEnd('\\', ':', ' ');
            if (letter.Length == 0)
                throw new BurnitException("No drive letter available for verification.");
            string path = "\\\\.\\" + letter.Substring(0, 1).ToUpperInvariant() + ":";

            SafeFileHandle h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                h.Dispose();
                throw new BurnitException("Could not open " + path + " for reading (error " + err + ").");
            }
            return new FileStream(h, FileAccess.Read, 64 * 1024, false);
        }
    }

    /// <summary>Reads a COM IStream as an ordinary .NET Stream (read-only, seekable).</summary>
    internal sealed class ComStreamReader : Stream
    {
        private readonly STREAM _s;
        private readonly long _length;
        private long _position;
        private readonly IntPtr _pcb = Marshal.AllocCoTaskMem(8);

        public ComStreamReader(STREAM s)
        {
            _s = s;
            System.Runtime.InteropServices.ComTypes.STATSTG st;
            _s.Stat(out st, 1 /* STATFLAG_NONAME */);
            _length = st.cbSize;
        }

        public static long SizeOf(STREAM s)
        {
            System.Runtime.InteropServices.ComTypes.STATSTG st;
            s.Stat(out st, 1);
            return st.cbSize;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _length; } }

        public override long Position
        {
            get { return _position; }
            set { Seek(value, SeekOrigin.Begin); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (offset == 0)
            {
                _s.Read(buffer, count, _pcb);
                int got = Marshal.ReadInt32(_pcb);
                _position += got;
                return got;
            }
            byte[] tmp = new byte[count];
            _s.Read(tmp, count, _pcb);
            int n = Marshal.ReadInt32(_pcb);
            Buffer.BlockCopy(tmp, 0, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _s.Seek(offset, (int)origin, _pcb);
            _position = Marshal.ReadInt64(_pcb);
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (_pcb != IntPtr.Zero) Marshal.FreeCoTaskMem(_pcb);
            base.Dispose(disposing);
        }
    }
}
