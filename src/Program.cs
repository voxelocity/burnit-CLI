// BURNIT - a CD/DVD/BD burner for the terminal.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Burnit
{
    public sealed class Options
    {
        public string Command = "";
        public List<string> Inputs = new List<string>();
        public string Drive;
        public string Speed;
        public string Label;
        public bool Verify;
        public bool Close;
        public bool Append;
        public bool Eject;
        public bool Yes;
        public bool DryRun;
        public bool Full;
        public bool NoFinalize;
        public bool Plain;
        public string ThemeName = "amber";
        public FsiFileSystems FileSystems = FsiFileSystems.None;   // None => let IMAPI choose
        public string Keep;          // spotify: where to leave the downloads
        public bool AsData;          // spotify: data disc of files instead of an audio CD

        public Options Clone()
        {
            Options c = (Options)MemberwiseClone();
            c.Inputs = new List<string>(Inputs);
            return c;
        }
    }

    public static class Program
    {
        public const string ClientName = "BURNIT";

        public static Theme ActiveTheme = Theme.Amber();
        public static bool Fancy;
        public static volatile bool Cancelled;
        public static Action CancelHook;

        [STAThread]
        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch (Exception) { }

            Options o;
            try { o = Parse(args); }
            catch (BurnitException e) { Console.Error.WriteLine("  " + e.Message); return 64; }

            ActiveTheme = Theme.Get(o.ThemeName);

            bool vt = Native.EnableVirtualTerminal();
            int cw, ch;
            Native.GetConsoleSize(out cw, out ch);

            // Escape hatch for recording a session, or for terminals we misjudge.
            bool force = Environment.GetEnvironmentVariable("BURNIT_FORCE_FANCY") == "1";
            bool redirected = Console.IsOutputRedirected && !force;

            Fancy = (vt || force) && !o.Plain && !redirected && cw >= 52 && ch >= 16;
            Out.Colour = (vt || force) && !redirected;

            Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                Cancelled = true;
                Action hook = CancelHook;
                if (hook != null)
                {
                    try { hook(); }
                    catch (Exception) { }
                }
            };

            try
            {
                switch (o.Command)
                {
                    case "drives": return Commands.Drives(o);
                    case "info": return Commands.Info(o);
                    case "burn": return Commands.BurnData(o);
                    case "spotify": return Commands.Spotify(o);
                    case "iso": return Commands.BurnIso(o);
                    case "audio": return Commands.BurnAudio(o);
                    case "erase": return Commands.Erase(o);
                    case "image": return Commands.Image(o);
                    case "eject": return Commands.Tray(o, true);
                    case "close": return Commands.Tray(o, false);
                    case "help": Help(); return 0;
                    default:
                        Help();
                        return o.Command.Length == 0 ? 0 : 64;
                }
            }
            catch (BurnitException e)
            {
                Restore();
                Out.Blank();
                Out.Bad(e.Message);
                Out.Blank();
                return 1;
            }
            catch (Exception e)
            {
                Restore();
                Out.Blank();
                Out.Bad(Errors.Describe(e));
                if (Environment.GetEnvironmentVariable("BURNIT_DEBUG") == "1")
                    Console.Error.WriteLine(e.ToString());
                else
                    Out.Note("  set BURNIT_DEBUG=1 for the full stack trace");
                Out.Blank();
                return 1;
            }
            finally
            {
                Native.RestoreConsoleMode();
            }
        }

        private static void Restore()
        {
            if (!Fancy) return;
            try
            {
                Console.Out.Write(Ansi.Reset);
                Console.Out.Write(Ansi.ShowCursor);
                Console.Out.Write(Ansi.MainScreen);
                Console.Out.Flush();
            }
            catch (Exception) { }
        }

        private static Options Parse(string[] args)
        {
            Options o = new Options();
            if (args.Length == 0) return o;

            int i = 0;
            if (!args[0].StartsWith("-", StringComparison.Ordinal))
            {
                o.Command = args[0].ToLowerInvariant();
                i = 1;
            }

            for (; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "-d":
                    case "--drive": o.Drive = Next(args, ref i, "--drive"); break;
                    case "-s":
                    case "--speed": o.Speed = Next(args, ref i, "--speed"); break;
                    case "-l":
                    case "--label": o.Label = Next(args, ref i, "--label"); break;
                    case "--verify": o.Verify = true; break;
                    case "--close": o.Close = true; break;
                    case "--append": o.Append = true; break;
                    case "--eject": o.Eject = true; break;
                    case "-y":
                    case "--yes": o.Yes = true; break;
                    case "--dry-run": o.DryRun = true; break;
                    case "--full": o.Full = true; break;
                    case "--no-finalize":
                    case "--no-finalise": o.NoFinalize = true; break;
                    case "--plain": o.Plain = true; break;
                    case "--theme": o.ThemeName = Next(args, ref i, "--theme"); break;
                    case "--kawaii":
                    case "--rave": o.ThemeName = "kawaii"; break;
                    case "--fs": o.FileSystems = ParseFs(Next(args, ref i, "--fs")); break;
                    case "--keep": o.Keep = Next(args, ref i, "--keep"); break;
                    case "--data": o.AsData = true; break;
                    case "-h":
                    case "--help": o.Command = "help"; break;
                    default:
                        if (a.StartsWith("-", StringComparison.Ordinal) && a.Length > 1)
                            throw new BurnitException("Unknown option " + a + ". Try: burnit help");
                        o.Inputs.Add(a);
                        break;
                }
            }
            return o;
        }

        private static string Next(string[] args, ref int i, string name)
        {
            if (i + 1 >= args.Length) throw new BurnitException(name + " needs a value.");
            return args[++i];
        }

        private static FsiFileSystems ParseFs(string s)
        {
            FsiFileSystems fs = FsiFileSystems.None;
            foreach (string part in s.Split(',', '+', ' '))
            {
                switch (part.Trim().ToLowerInvariant())
                {
                    case "": break;
                    case "iso":
                    case "iso9660": fs |= FsiFileSystems.Iso9660; break;
                    case "joliet": fs |= FsiFileSystems.Joliet; break;
                    case "udf": fs |= FsiFileSystems.Udf; break;
                    default: throw new BurnitException("Unknown filesystem '" + part + "'. Use iso, joliet or udf.");
                }
            }
            return fs;
        }

        private static void Help()
        {
            Theme t = ActiveTheme;
            bool c = Out.Colour;
            Func<int, string, string> col = delegate(int rgb, string s)
            {
                return c ? Ansi.Fg(rgb) + s + Ansi.Reset : s;
            };

            Console.WriteLine();
            Console.WriteLine("  " + col(t.Accent, "BURNIT") + col(t.Dim, "   burn discs from the terminal"));
            Console.WriteLine();
            Console.WriteLine("  " + col(t.Label, "USAGE"));
            Console.WriteLine("    burnit <command> [options]");
            Console.WriteLine();
            Console.WriteLine("  " + col(t.Label, "COMMANDS"));
            Console.WriteLine("    drives                    list optical recorders and what is in them");
            Console.WriteLine("    info                      everything the drive knows about the loaded disc");
            Console.WriteLine("    burn <path>...            burn files and folders as a data disc");
            Console.WriteLine("    iso <image.iso>           write an existing disc image");
            Console.WriteLine("    audio <track>...          burn a Red Book audio CD (ffmpeg decodes)");
            Console.WriteLine("    spotify <url|search>...   fetch with spotdl, then burn it as an audio CD");
            Console.WriteLine("    image <out.iso> <path>... build an ISO file without burning anything");
            Console.WriteLine("    erase                     blank a CD-RW / DVD-RW / BD-RE");
            Console.WriteLine("    eject | close             open or close the tray");
            Console.WriteLine();
            Console.WriteLine("  " + col(t.Label, "OPTIONS"));
            Console.WriteLine("    -d, --drive <E:|0>        which recorder to use");
            Console.WriteLine("    -s, --speed <8x|max|min>  write speed, snapped to what the drive supports");
            Console.WriteLine("    -l, --label <name>        volume label (16 chars)");
            Console.WriteLine("        --verify              read the disc back and compare sha256");
            Console.WriteLine("        --close               close the disc so nothing more can be added");
            Console.WriteLine("        --append              add a session to a disc that already has data");
            Console.WriteLine("        --fs <iso,joliet,udf> filesystems to generate");
            Console.WriteLine("        --no-finalise         audio: leave the disc open");
            Console.WriteLine("        --keep <dir>          spotify: keep the downloads here");
            Console.WriteLine("        --data                spotify: burn the files as a data disc");
            Console.WriteLine("        --full                erase: full blank instead of quick");
            Console.WriteLine("        --eject               eject when finished");
            Console.WriteLine("        --dry-run             do everything except fire the laser");
            Console.WriteLine("    -y, --yes                 do not ask for confirmation");
            Console.WriteLine("        --theme <amber|ice|mono|kawaii>");
            Console.WriteLine("        --kawaii              rainbow rave mode. you asked for it");
            Console.WriteLine("        --plain               no animation, line output only");
            Console.WriteLine();
            Console.WriteLine("  " + col(t.Label, "EXAMPLES"));
            Console.WriteLine(col(t.Dim, "    burnit drives"));
            Console.WriteLine(col(t.Dim, "    burnit burn ./photos --label HOLIDAY --verify --eject"));
            Console.WriteLine(col(t.Dim, "    burnit iso ./debian.iso --speed 8x --verify"));
            Console.WriteLine(col(t.Dim, "    burnit audio *.flac --speed max"));
            Console.WriteLine(col(t.Dim, "    burnit spotify https://open.spotify.com/playlist/... --speed 16x"));
            Console.WriteLine(col(t.Dim, "    burnit image out.iso ./project     # no disc needed"));
            Console.WriteLine();
        }
    }
}
