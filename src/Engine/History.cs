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
    }

    /// <summary>
    /// Executed scripts, one file each: a header line (time, server, database) and then the script exactly
    /// as it ran. One file per run means nothing to escape and nothing to corrupt when two SSMS windows
    /// execute at once.
    /// </summary>
    public static class History
    {
        public static readonly string Folder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SQLFinish", "History");

        const string Mark = "-- SQLFinish\t", Stamp = "yyyy-MM-dd HH:mm:ss.fff";

        /// <summary>Stores <paramref name="e"/> and deletes all but the newest <paramref name="keep"/> entries.</summary>
        public static void Save(string folder, HistoryEntry e, int keep = 1000)
        {
            Directory.CreateDirectory(folder);
            string header = Mark + e.When.ToString(Stamp, CultureInfo.InvariantCulture) + "\t" + Clean(e.Server) + "\t" + Clean(e.Database);
            File.WriteAllText(Path.Combine(folder, e.When.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".sql"), header + "\n" + e.Sql);
            foreach (var old in Files(folder).Skip(keep)) File.Delete(old);
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
                list.Add(new HistoryEntry { When = when, Server = head[1], Database = head[2], Sql = text.Substring(eol + 1) });
            }
            return list;
        }

        /// <summary>Newest first: the file names are timestamps.</summary>
        static IEnumerable<string> Files(string folder) =>
            Directory.GetFiles(folder, "????????-??????-???.sql").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal);

        static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
