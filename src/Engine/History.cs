using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SQLFinish.Engine
{
    /// <summary>A script that was executed.</summary>
    public sealed class HistoryEntry
    {
        public DateTime When;
        public string Server, Database, Sql;
        public string FilePath; // where it is stored, once saved or loaded
        public bool Pinned;     // kept until it is deleted by hand, however much is run after it
    }

    /// <summary>
    /// Executed scripts, one file each: a header line (time, server, database) and then the script exactly
    /// as it ran. One file per run means nothing to escape and nothing to corrupt when two SSMS windows
    /// execute at once. A pinned entry is the same file renamed from name.sql to name.pin.sql.
    /// </summary>
    public static class History
    {
        /// <summary>In the user's profile, unless the environment variable SQLFINISH_HISTORY names another folder.</summary>
        public static readonly string Folder = Environment.GetEnvironmentVariable("SQLFINISH_HISTORY") is string elsewhere && elsewhere.Trim().Length > 0
            ? elsewhere.Trim()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SQLFinish", "History");

        const string Mark = "-- SQLFinish\t", Stamp = "yyyy-MM-dd HH:mm:ss.fff", Pin = ".pin.sql";

        static bool IsPinned(string file) => file.EndsWith(Pin, StringComparison.OrdinalIgnoreCase);

        /// <summary>Stores <paramref name="e"/> and deletes all but the newest <paramref name="keep"/> entries that are not pinned.</summary>
        public static void Save(string folder, HistoryEntry e, int keep = 1000)
        {
            Directory.CreateDirectory(folder);
            string header = Mark + e.When.ToString(Stamp, CultureInfo.InvariantCulture) + "\t" + Clean(e.Server) + "\t" + Clean(e.Database);
            e.FilePath = Path.Combine(folder, e.When.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".sql");
            File.WriteAllText(e.FilePath, header + "\n" + e.Sql);
            foreach (var old in Files(folder).Where(f => !IsPinned(f)).Skip(keep)) File.Delete(old);
        }

        /// <summary>Forgets <paramref name="e"/>, pinned or not.</summary>
        public static void Delete(HistoryEntry e) => File.Delete(e.FilePath);

        /// <summary>Pins or unpins <paramref name="e"/>.</summary>
        public static void SetPinned(HistoryEntry e, bool pinned)
        {
            if (e.Pinned == pinned) return;
            string to = pinned ? Path.ChangeExtension(e.FilePath, Pin) : e.FilePath.Substring(0, e.FilePath.Length - Pin.Length) + ".sql";
            File.Move(e.FilePath, to);
            e.FilePath = to;
            e.Pinned = pinned;
        }

        /// <summary>The stored entries, newest first. Files that are not ours are skipped.</summary>
        public static List<HistoryEntry> Load(string folder)
        {
            var list = new List<HistoryEntry>();
            if (!Directory.Exists(folder)) return list;
            foreach (var file in Files(folder))
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch (IOException) { continue; } // being written by another window right now
                int eol = text.IndexOf('\n');
                if (eol < 0 || !text.StartsWith(Mark, StringComparison.Ordinal)) continue;
                var head = text.Substring(Mark.Length, eol - Mark.Length).Split('\t');
                if (head.Length != 3 || !DateTime.TryParseExact(head[0], Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) continue;
                list.Add(new HistoryEntry { When = when, Server = head[1], Database = head[2], Sql = text.Substring(eol + 1), FilePath = file, Pinned = IsPinned(file) });
            }
            return list;
        }

        /// <summary>Newest first: the file names are timestamps, pinned or not.</summary>
        static IEnumerable<string> Files(string folder) =>
            Directory.GetFiles(folder, "*.sql")
                     .Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^\d{8}-\d{6}-\d{3}(\.pin)?\.sql$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                     .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal);

        static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
