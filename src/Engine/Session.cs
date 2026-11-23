using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SQLFinish.Engine
{
    /// <summary>A query window as it was last seen open.</summary>
    public sealed class SessionWindow
    {
        public string Path = "";       // the file it shows, when that file holds exactly its text; "" for a query never saved, or changed since
        public string Connection = ""; // SSMS's own description of its connection (never a password), or ""
        public string Text;            // Save: null when unchanged since the last Save. Not filled in by Load: see TextFile
        public string TextFile;        // Load: the file holding its text
        public bool Active;            // it was the window in front
    }

    /// <summary>
    /// The query windows each running SSMS has open, kept on disk so that the next start can bring them
    /// back. Every SSMS process writes to a folder of its own (named for its process id and start time),
    /// two files per window: n.sql with the text exactly, and n.meta with where it came from. A folder whose
    /// process is gone is a session waiting to be reopened.
    /// </summary>
    public sealed class Session
    {
        /// <summary>In the user's profile, unless the environment variable SQLFINISH_SESSION names another folder.</summary>
        public static readonly string Root = Environment.GetEnvironmentVariable("SQLFINISH_SESSION") is string elsewhere && elsewhere.Trim().Length > 0
            ? elsewhere.Trim()
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SQLFinish", "Session");

        /// <summary>
        /// How long a window must be gone, with SSMS still running, before it is forgotten. Closing SSMS
        /// closes every window moments before the process ends, and those must be kept: only a window that
        /// stays closed while SSMS lives on was closed on purpose.
        /// </summary>
        public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

        const string Claimed = ".restoring", Failed = ".failed";
        static readonly Regex Owner = new Regex(@"^(\d+)-(\d+)$");

        sealed class Slot
        {
            public int Number;
            public string Meta;      // as last written
            public DateTime? Gone;   // when it was first seen missing
        }

        readonly string folder;
        readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
        int next;

        public Session(string root, int processId, DateTime started)
        {
            folder = System.IO.Path.Combine(root, processId + "-" + started.Ticks);
        }

        string FileOf(Slot slot, string extension) => System.IO.Path.Combine(folder, slot.Number.ToString("D4") + extension);

        /// <summary>
        /// Records the windows that are open now, each under a key that stays the same while the window
        /// lives. Windows not listed are forgotten once they have been missing for <see cref="Grace"/>.
        /// </summary>
        public void Save(IEnumerable<KeyValuePair<string, SessionWindow>> open, DateTime now)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in open)
            {
                var w = pair.Value;
                seen.Add(pair.Key);
                bool known = slots.TryGetValue(pair.Key, out var slot);
                if (!known) slots[pair.Key] = slot = new Slot { Number = next++ };
                slot.Gone = null;
                Directory.CreateDirectory(folder);
                if (w.Text != null || !known) File.WriteAllText(FileOf(slot, ".sql"), w.Text ?? "", new UTF8Encoding(true));
                string meta = (w.Active ? "active" : "") + "\n" + w.Path + "\n" + w.Connection;
                if (meta != slot.Meta) File.WriteAllText(FileOf(slot, ".meta"), slot.Meta = meta);
            }
            foreach (var pair in slots.Where(s => !seen.Contains(s.Key)).ToList())
            {
                var slot = pair.Value;
                if (slot.Gone == null) slot.Gone = now;
                if (now - slot.Gone.Value < Grace) continue;
                File.Delete(FileOf(slot, ".sql"));
                File.Delete(FileOf(slot, ".meta"));
                slots.Remove(pair.Key);
            }
        }

        /// <summary>
        /// The folders of sessions whose SSMS is no longer running. <paramref name="startOf"/> gives the
        /// start time of a running process, or null when there is no such process. A session whose
        /// reopening was begun but never finished (SSMS died in the middle of it) is set aside as
        /// name.failed rather than tried again on every start.
        /// </summary>
        public static List<string> Abandoned(string root, Func<int, DateTime?> startOf)
        {
            var found = new List<string>();
            if (!Directory.Exists(root)) return found;
            foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                string name = System.IO.Path.GetFileName(dir);
                if (name.EndsWith(Claimed, StringComparison.Ordinal))
                {
                    string aside = dir.Substring(0, dir.Length - Claimed.Length) + Failed;
                    if (!Directory.Exists(aside)) Directory.Move(dir, aside);
                    continue;
                }
                var m = Owner.Match(name);
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out int id) || !long.TryParse(m.Groups[2].Value, out long ticks)) continue;
                if (startOf(id)?.Ticks == ticks) continue; // still running: its windows are open over there
                found.Add(dir);
            }
            return found;
        }

        /// <summary>
        /// Takes an abandoned session for reopening, so that no other SSMS starting at the same moment
        /// reopens it too. Returns its new folder; throws IOException when another process got there first.
        /// </summary>
        public static string Claim(string abandoned)
        {
            Directory.Move(abandoned, abandoned + Claimed);
            return abandoned + Claimed;
        }

        /// <summary>The windows of a session folder, in the order they were first opened.</summary>
        public static List<SessionWindow> Load(string folder)
        {
            var windows = new List<SessionWindow>();
            foreach (var meta in Directory.GetFiles(folder, "*.meta").OrderBy(f => f, StringComparer.Ordinal))
            {
                string text = System.IO.Path.ChangeExtension(meta, ".sql");
                var parts = File.ReadAllText(meta).Split(new[] { '\n' }, 3);
                if (parts.Length != 3 || !File.Exists(text)) continue;
                windows.Add(new SessionWindow { Active = parts[0] == "active", Path = parts[1], Connection = parts[2], TextFile = text });
            }
            return windows;
        }
    }
}
