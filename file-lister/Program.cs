using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;

namespace file_lister
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("ERROR: no config specified");
                return;
            }

            if (!File.Exists(args[0]))
            {
                Console.Error.WriteLine("ERROR: config file \"" + args[0] + "\" does not exist");
                return;
            }

            App app = new App();

            if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
            {
                app.Start(args[0], args[1]);
                return;
            }

            app.Start(args[0], string.Empty);
        }

        class App
        {
            public void Start(string configPath, string listingName)
            {
                try
                {
                    Lister lister = new Lister(JsonConvert.DeserializeObject<Config>(File.ReadAllText(configPath)));
                    lister.List(listingName);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("CONFIG ERROR: " + ex.Message);
                }
            }

            class Lister
            {
                // Lister-level vars

                private Config _config = null;
                private Regex[] _skipDirs = null;
                private Regex[] _separateDirs = null;

                private TimeSpan _localOffset;
                private Stopwatch _performanceTimer = null;

                private LegacyStringComparer _legacyStringComparer = null;

                // listing-level vars

                private ulong _listingFileCount = 0;
                private long _listingByteCount = 0;

                private Dictionary<string, FileOutput> _logFileOutputs = null;
                private Stack<FileOutput> _dataFileOutputs = null;
                private TextWriter _currentDataFileWriter = null; // hot loop performance optimization (in lieu of calling _dataFileOutputs.Peek().WriteLine())

                private string _listingOutputPath = string.Empty;  // the ultimate directory to dump data and log output to
                private string _listingOutputPathNormalized = null;  // hot loop performance optimization

                // constructors

                public Lister(Config config)
                {
                    _config = config;

                    MapRegexPatterns(ref _skipDirs, _config.skipDirs);
                    MapRegexPatterns(ref _separateDirs, _config.separateDirs);

                    DateTime now = DateTime.Now;
                    _localOffset = now - now.ToUniversalTime();

                    _performanceTimer = new Stopwatch();

                    if (_config.legacy)
                    {
                        _legacyStringComparer = new LegacyStringComparer();
                    }
                }

                // methods

                public void List(string listingName)
                {
                    try
                    {
                        if (HasInvalidFilenameChars(listingName))
                        {
                            Console.Error.WriteLine("ERROR: Listing name contains invalid characters");
                            return;
                        }

                        _listingOutputPath =
                            string.IsNullOrWhiteSpace(_config.output)
                            ? string.Empty
                            : Path.Combine(_config.output, string.Format("{0:yyyy-MM-dd HH.mm.ss}", DateTime.Now) + (listingName.Length > 0 ? " " : string.Empty) + listingName);

                        if (IsOutputToFile())
                        {
                            try
                            {
                                Directory.CreateDirectory(_listingOutputPath);
                                Console.Out.WriteLine("Output target: \"" + _listingOutputPath + "\"");
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine("ERROR: Could not create output directory: \"" + _listingOutputPath + "\": " + ex.Message);
                                return;
                            }
                        }

                        _listingOutputPathNormalized =
                            IsOutputToFile()
                            ? NormalizePath(_listingOutputPath)
                            : null;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("LISTING PREP ERROR: " + ex.Message);
                        return;
                    }

                    try
                    {
                        _currentDataFileWriter = Console.Out;

                        if (IsOutputToFile())
                        {
                            _logFileOutputs = new Dictionary<string, FileOutput>();
                            _dataFileOutputs = new Stack<FileOutput>();
                        }

                        try
                        {
                            WriteConfig("// effective listing config at run time\n");
                            WriteConfig(JsonConvert.SerializeObject(_config, Formatting.Indented));
                        }
                        catch (Exception ex)
                        {
                            //WriteError("Error creating effective listing file: " + ex.Message);
                        }

                        if (_config.roots == null)
                        {
                            ListRoot(string.IsNullOrEmpty(_config.root) ? Directory.GetCurrentDirectory() : _config.root);
                        }
                        else
                        {
                            for (int i = 0; i < _config.roots.Length; i++)
                            {
                                if (i > 0 && !IsOutputToFile())
                                {
                                    Console.Out.WriteLine("\n" + new String('-', 75));
                                }
                                ListRoot(string.IsNullOrEmpty(_config.roots[i]) ? Directory.GetCurrentDirectory() : _config.roots[i]);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteError("LISTING ERROR: " + ex.Message);
                    }
                    finally
                    {
                        Cleanup();
                    }
                }

                private void ListRoot(string root)
                {
                    if (!Directory.Exists(root))
                    {
                        WriteError("CONFIG ERROR: root directory \"" + root + "\" does not exist");
                        return;
                    }

                    if (IsOutputToFile() && !PushDataFileOutput(root))
                    {
                        WriteError("ERROR: failed to create root output file for \"" + root + "\"");
                        return;
                    }

                    _performanceTimer.Restart();

                    _listingFileCount = 0;
                    _listingByteCount = 0;

                    ListHelper(root);

                    _performanceTimer.Stop();

                    // write stats

                    if (_config.legacy) // legacy mode appends stats to end of listing output only
                    {
                        _currentDataFileWriter.WriteLine("\n     Total Files Listed:");
                        _currentDataFileWriter.WriteLine("{0, 16} File(s) {1, 14:N0} bytes", _listingFileCount, _listingByteCount);
                    }
                    else
                    {
                        string listingFileCountFormatted = _listingFileCount.ToString("N0");
                        string listingByteCountFormatted = _listingByteCount.ToString("N0");
                        string timingFormatted = _performanceTimer.Elapsed.ToString("hh\\:mm\\:ss");

                        int outputColWidth = Math.Max(listingFileCountFormatted.Length, Math.Max(listingByteCountFormatted.Length, timingFormatted.Length));

                        WriteStats(string.Format("{3}Statistics for {4}\n{3}Files: {0, " + outputColWidth + "}{3}Bytes: {1, " + outputColWidth + "}{3}Time:  {2, " + outputColWidth + "}", listingFileCountFormatted, listingByteCountFormatted, timingFormatted, IsOutputToFile() ? "\n" : "\n  ", root));
                    }

                    if (IsOutputToFile())
                    {
                        PopDataFileOutput();
                    }
                }

                private void ListHelper(string dir)
                {
                    // System.Diagnostics.Debug.WriteLine(dir);

                    try
                    {
                        string[] files = Directory.GetFiles(dir);
                        string[] subdirs = Directory.GetDirectories(dir);

                        if ((_config.printEmptyDirs && !_config.legacy) || files.Length > 0)
                        {
                            WriteDirHeader(dir);

                            if (files.Length > 0)
                            {
                                ulong dirFileCount = 0;
                                long dirByteCount = 0;

                                Array.Sort(files, StringComparer.InvariantCultureIgnoreCase);

                                foreach (string file in files)
                                {
                                    try
                                    {
                                        FileInfo fi = new FileInfo(file);

                                        dirFileCount++;
                                        dirByteCount += fi.Length;

                                        // don't use fi.LastWriteTime
                                        // see: How to get the correct file time in C# in .NET Framework 2.0 - CodeProject:  http://www.codeproject.com/Articles/32394/How-to-get-the-correct-file-time-in-C-in-NET-Frame

                                        if (_config.legacy)
                                        {
                                            _currentDataFileWriter.WriteLine("{0:yyyy-MM-dd  hh:mm tt} {1} {2}",
                                                fi.LastWriteTimeUtc + _localOffset,
                                                fi.Attributes.HasFlag(FileAttributes.ReparsePoint) ? "   <SYMLINK>     " : string.Format("{0, 17:N0}", fi.Length),
                                                Path.GetFileName(file));
                                        }
                                        else
                                        {
                                            _currentDataFileWriter.WriteLine("{0:yyyy-MM-dd HH.mm.ss} {1} {2}",
                                                fi.LastWriteTimeUtc + _localOffset,
                                                fi.Attributes.HasFlag(FileAttributes.ReparsePoint) ? "          [SYMLINK]" : string.Format("{0, 19:N0}", fi.Length),
                                                Path.GetFileName(file));
                                        }
                                    }
                                    catch (System.Security.SecurityException ex)
                                    {
                                        WriteWarning(ex.Message);
                                    }
                                    catch (System.UnauthorizedAccessException ex)
                                    {
                                        WriteWarning(ex.Message);
                                    }
                                    catch (System.IO.PathTooLongException ex)
                                    {
                                        WriteError(ex.Message);
                                    }
                                    catch (Exception ex)
                                    {
                                        WriteError(ex.Message);
                                    }
                                }

                                _listingFileCount += dirFileCount;
                                _listingByteCount += dirByteCount;

                                if (_config.legacy)
                                {
                                    _currentDataFileWriter.WriteLine("{0, 16} File(s) {1, 14:N0} bytes", dirFileCount, dirByteCount);
                                }
                            }
                            else
                            {
                                _currentDataFileWriter.WriteLine("[NO FILES]");
                            }
                        }

                        if (_config.legacy)
                        {
                            Array.Sort(subdirs, _legacyStringComparer);
                        }
                        else
                        {
                            Array.Sort(subdirs, StringComparer.InvariantCultureIgnoreCase);
                        }

                        foreach (string subdir in subdirs)
                        {
                            try
                            {
                                if (_listingOutputPathNormalized == null || _listingOutputPathNormalized != NormalizePath(subdir))  // completely disregard the current listing output path
                                {
                                    DirectoryInfo di = new DirectoryInfo(subdir);

                                    if (IsJunctionPoint(di))
                                    {
                                        WriteWarning("Skipping junction point \"" + subdir + "\"");
                                    }
                                    else if (SkipDir(subdir))
                                    {
                                        WriteDirHeader(subdir);
                                        _currentDataFileWriter.WriteLine("[SKIPPED]");
                                    }
                                    else
                                    {
                                        if (di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                                        {
                                            WriteWarning("Unskipped directory symbolic link encountered: \"" + subdir + "\"");
                                        }

                                        if (IsOutputToFile() && SeparateDir(subdir))
                                        {
                                            WriteDirHeader(subdir);
                                            _currentDataFileWriter.WriteLine("[SEPARATED]");

                                            if (PushDataFileOutput(subdir))
                                            {
                                                ListHelper(subdir);
                                                PopDataFileOutput();
                                            }
                                            else
                                            {
                                                WriteError("ERROR pushing output file. Skipping listing for \"" + subdir + "\".");
                                            }
                                        }
                                        else
                                        {
                                            ListHelper(subdir);
                                        }
                                    }
                                }
                            }
                            catch (System.Security.SecurityException ex)
                            {
                                WriteWarning(ex.Message);
                            }
                            catch (System.IO.PathTooLongException ex)
                            {
                                WriteError(ex.Message);
                            }
                            catch (Exception ex)
                            {
                                WriteError(ex.Message);
                            }
                        }
                    }
                    catch (System.UnauthorizedAccessException ex)
                    {
                        WriteWarning(ex.Message);
                    }
                    catch (System.IO.PathTooLongException ex)
                    {
                        WriteError(ex.Message);
                    }
                    catch (Exception ex)
                    {
                        WriteError(ex.Message);
                    }
                }

                private bool HasInvalidFilenameChars(string s)
                {
                    return (!string.IsNullOrEmpty(s) && s.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0);
                }

                private bool IsJunctionPoint(DirectoryInfo di)
                {
                    return di.Attributes.HasFlag(FileAttributes.Hidden)
                        && di.Attributes.HasFlag(FileAttributes.System)
                        && di.Attributes.HasFlag(FileAttributes.ReparsePoint);
                }

                private bool IsJunctionPoint(string path)
                {
                    return IsJunctionPoint(new DirectoryInfo(path));
                }

                private void MapRegexPatterns(ref Regex[] to, string[] from)
                {
                    if (from == null)
                    {
                        to = null;
                        return;
                    }

                    to = new Regex[from.Length];

                    for (int i = 0; i < to.Length; i++)
                    {
                        to[i] = new Regex(from[i], RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ECMAScript);
                    }
                }

                private bool SkipDir(string dir)
                {
                    return MatchesAny(dir, _skipDirs);
                }

                private bool SeparateDir(string dir)
                {
                    return MatchesAny(dir, _separateDirs);
                }

                private bool MatchesAny(string s, Regex[] regexs)
                {
                    if (regexs != null)
                    {
                        for (int i = 0; i < regexs.Length; i++)
                        {
                            if (regexs[i].IsMatch(s))
                            {
                                return true;
                            }
                        }
                    }

                    return false;
                }

                private void WriteDirHeader(string dir)
                {
                    if (_config.legacy)
                    {
                        _currentDataFileWriter.WriteLine("\n Directory of " + dir + "\n");
                    }
                    else
                    {
                        _currentDataFileWriter.WriteLine("\n " + dir + "\n");
                    }
                }

                private string PathToFilename(string path)
                {
                    return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace("\\", " - ").Replace(":", string.Empty) + ".txt";
                }

                private bool PushDataFileOutput(string path)
                {
                    try
                    {
                        _dataFileOutputs.Push(new FileOutput(Path.Combine(_listingOutputPath, PathToFilename(path))));
                        _currentDataFileWriter = _dataFileOutputs.Peek().Writer;
                        return true;
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            WriteError("ERROR: failed to create output file for \"" + path + "\": " + ex.Message);
                        }
                        catch (Exception)
                        { }
                    }

                    return false;
                }

                private bool PopDataFileOutput()
                {
                    if (_dataFileOutputs == null || _dataFileOutputs.Count < 1)
                    {
                        return false;
                    }

                    _dataFileOutputs.Pop().Flush().Close();

                    if (_dataFileOutputs.Count > 0)
                    {
                        _currentDataFileWriter = _dataFileOutputs.Peek().Writer;
                    }
                    else
                    {
                        _currentDataFileWriter = Console.Out;
                    }

                    return true;
                }

                private void WriteError(string msg)
                {
                    WriteLogOutput(msg, "_ERRORS.txt", Console.Error);
                }

                private void WriteWarning(string msg)
                {
                    WriteLogOutput(msg, "_WARNINGS.txt");
                }

                private void WriteConfig(string msg)
                {
                    WriteLogOutput(msg, "_CONFIG.json");
                }

                private void WriteStats(string msg)
                {
                    WriteLogOutput(msg, "_STATS.txt", Console.Out);
                }

                // a log file contains run-related, non-listing data (eg, errors, warnings, stats). Contrary to legacy snapshooter, only 1 log file per log type (ie, filename) is created, and only if data exists for the log type.

                private void WriteLogOutput(string msg, string filename, TextWriter altOutput = null)
                {
                    if (IsOutputToFile())
                    {
                        if (!_logFileOutputs.ContainsKey(filename))
                        {
                            _logFileOutputs.Add(filename, new FileOutput(Path.Combine(_listingOutputPath, filename)));
                        }

                        _logFileOutputs[filename].WriteLine(msg);
                    }

                    if (altOutput != null)
                    {
                        altOutput.WriteLine(msg);
                    }
                }

                private bool IsOutputToFile()
                {
                    return (!string.IsNullOrEmpty(_listingOutputPath));
                }

                // http://stackoverflow.com/a/21058121/384062

                private string NormalizePath(string path)
                {
                    return Path.GetFullPath(new Uri(path).LocalPath)
                               .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               .ToUpperInvariant();
                }

                private void Cleanup()
                {
                    // clean up data file outputs

                    if (_dataFileOutputs != null)
                    {
                        while (PopDataFileOutput()) { }
                        _dataFileOutputs = null;
                        _currentDataFileWriter = null;
                    }

                    // clean up log file outputs

                    if (_logFileOutputs != null)
                    {
                        foreach (FileOutput logFileOutput in _logFileOutputs.Values)
                        {
                            logFileOutput.Flush().Close();
                        }

                        _logFileOutputs.Clear();
                        _logFileOutputs = null;
                    }
                }
            }

            class LegacyStringComparer : IComparer<string>
            {
                // not sure about order of chars here. basically I just played with it until directory ordering matched directory ordering of our legacy snapshooter output. this was mainly to facilitate testing.
                private char[] _charOrder = new char[] { ' ', '!', '"', '#', '$', '%', '&', '\'', '(', ')', '*', '+', ',', '-', '.', '/',
                    ':', '<', '=', '>', '?', '@', 
                    '\\', ']', '^',  '`', 
                    '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 
                    ';', 
                    //'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z', 
                    'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 'x', 'y', 'z', 
                    '[', '_',
                    '{', '|', '}', '~' };

                public int Compare(string a, string b)
                {
                    int len = Math.Min(a.Length, b.Length);
                    int compareResult;

                    for (int i = 0; i < len; i++)
                    {
                        if ((compareResult = CompareChars(a[i], b[i])) != 0)
                        {
                            return compareResult;
                        }
                    }

                    return a.Length == b.Length ? 0 : (a.Length < b.Length ? -1 : 1);
                }

                private int CompareChars(char a, char b)
                {
                    a = char.ToLower(a);
                    b = char.ToLower(b);

                    if (a == b)
                    {
                        return 0;
                    }

                    int idxa = Array.IndexOf(_charOrder, a);
                    int idxb = Array.IndexOf(_charOrder, b);

                    if (idxa >= 0 && idxb >= 0)
                    {
                        return idxa < idxb ? -1 : 1;
                    }

                    return a.CompareTo(b);
                }

                //public void Test(StringComparer stringComparer)
                //{
                //    char[] printArray = new char[_charOrder.Length];

                //    for (int i = 0; i < _charOrder.Length; i++)
                //    {
                //        printArray[i] = _charOrder[i];
                //    }

                //    Array.Sort(printArray, stringComparer);

                //    Console.Out.Write('[');

                //    for (int i = 0; i < printArray.Length; i++)
                //    {
                //        if (i > 0)
                //        {
                //            Console.Out.Write(", ");
                //        }

                //        Console.Out.Write('\'');

                //        if (printArray[i] == '\'' || printArray[i] == '\\')
                //        {
                //            Console.Out.Write('\\');
                //        }

                //        Console.Out.Write(printArray[i]);
                //        Console.Out.Write('\'');
                //    }

                //    Console.Out.Write(']');
                //}
            }

            class Config
            {
                // expecting one of either "root" or "roots" to be provided
                // if non-null "roots" array exists: list all roots. for each null or empty string in the array, list current directory. if empty array, list nothing.
                // else if non-null-or-empty "root" string exists: list root
                // else: list current directory
                public string root = Directory.GetCurrentDirectory();
                public string[] roots = null;

                public string output = string.Empty; // root directory of output. empty string implies stdout.
                public bool legacy = false; // parity with legacy snapshooter format used (dir <path> /s /a:-d /o:n /t:w /4). used mainly for testing.
                public bool printEmptyDirs = true; // print directories not containing files. prints the directory with a [NO FILES] label. does not apply to legacy mode, since legacy mode never prints directories not containing files.
                public string[] skipDirs = null; // regular expression pattern strings of directories to skip recursion of. prints the directory with a [SKIPPED] label.
                public string[] separateDirs = null; // regular expression pattern strings of directories to split off into separate output files. prints [the directory] to the parent listing with a [SEPARATED] label. has no effect if output is stdout.
            }

            class FileOutput
            {
                private FileStream _stream = null;
                private TextWriter _writer = null;

                public FileOutput(string path)
                {
                    _writer = new StreamWriter(_stream = File.Open(path, FileMode.Create, FileAccess.Write), Encoding.UTF8);
                }

                //public FileStream Stream
                //{
                //    get { return _stream; }
                //}

                public TextWriter Writer
                {
                    get { return _writer; }
                }

                //public FileOutput Write(string s)
                //{
                //    if (_writer != null)
                //    {
                //        _writer.Write(s);
                //    }

                //    return this; // support chaining
                //}

                public FileOutput WriteLine(string s)
                {
                    if (_writer != null)
                    {
                        _writer.WriteLine(s);
                    }

                    return this; // support chaining
                }

                public FileOutput Flush()
                {
                    if (_writer != null)
                    {
                        _writer.Flush();
                    }

                    // flushing underlying stream is presumably redundant, but documentation is unclear. Leaving for safety.

                    if (_stream != null)
                    {
                        _stream.Flush();
                    }

                    return this; // support chaining
                }

                public FileOutput Close()
                {
                    if (_writer != null)
                    {
                        _writer.Close();
                        _writer = null;
                    }

                    // closing underlying stream is presumably redundant, but documentation is unclear. Leaving for safety.

                    if (_stream != null)
                    {
                        _stream.Close();
                        _stream = null;
                    }

                    return this; // support chaining
                }

                public bool Valid()
                {
                    return _writer != null && _stream != null;
                }
            }
        }
    }
}
