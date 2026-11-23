using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using SQLFinish.Engine;

namespace SQLFinish.Vs
{
    /// <summary>
    /// Brings back, when SSMS starts, the query windows that were open when it was last closed, and keeps
    /// noting the ones open now (every few seconds, and once more as SSMS closes) for the start after this.
    /// </summary>
    internal sealed class SessionKeeper
    {
        // ponytail: a window over this many characters (a bulk data script) is not kept; raise it if that is ever missed.
        const int MaxLength = 5000000;

        readonly IServiceProvider services;
        readonly Func<bool> enabled;
        readonly Session session;
        readonly Dictionary<string, int> written = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // window -> version of its text last noted
        readonly DispatcherTimer timer;
        bool started; // the last session has been dealt with, so this one may be noted
        int noted = -1; // how many windows the last note held (for the trace only)

        public SessionKeeper(IServiceProvider services, Func<bool> enabled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.services = services;
            this.enabled = enabled;
            using (var me = Process.GetCurrentProcess()) session = new Session(Session.Root, me.Id, me.StartTime);
            timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.ApplicationIdle, (s, e) => Tick(), Dispatcher.CurrentDispatcher);
        }

        [DllImport("user32.dll")]
        static extern bool IsWindowEnabled(IntPtr window);

        /// <summary>True while a dialog is up, or SSMS is starting or closing: no time to open windows, or to conclude that one was closed.</summary>
        bool Busy()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (VsShellUtilities.ShellIsShuttingDown) return true;
            var shell = services.GetService(typeof(SVsShell)) as IVsShell;
            object modal = null, zombie = null;
            shell?.GetProperty((int)__VSSPROPID4.VSSPROPID_IsModal, out modal);
            shell?.GetProperty((int)__VSSPROPID.VSSPROPID_Zombie, out zombie);
            if (shell == null || modal as bool? == true || zombie as bool? == true) return true;
            using (var me = Process.GetCurrentProcess())
                return me.MainWindowHandle == IntPtr.Zero || !IsWindowEnabled(me.MainWindowHandle);
        }

        void Tick()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (Busy()) return;
                if (!started)
                {
                    started = true; // whatever happens next, never twice
                    Reopen(enabled());
                }
                Capture();
            }
            catch (Exception e)
            {
                timer.Stop(); // once is enough to say it; the windows are simply not noted for the rest of this run
                Ssms.Log("could not keep track of the open query windows", e);
            }
        }

        /// <summary>Notes the query windows that are open now.</summary>
        public void Capture()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Not before the last session has been reopened: until then its windows would count as closed.
            if (!started || !timer.IsEnabled || !enabled()) return;

            string front = null;
            try { front = (services.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ActiveDocument?.FullName; }
            catch (COMException) { } // no document is active
            if (!(services.GetService(typeof(SVsUIShell)) is IVsUIShell shell) || !(services.GetService(typeof(SComponentModel)) is IComponentModel model)) return;
            var adapters = model.GetService<IVsEditorAdaptersFactoryService>();
            ErrorHandler.ThrowOnFailure(shell.GetDocumentWindowEnum(out IEnumWindowFrames frames));

            var open = new List<KeyValuePair<string, SessionWindow>>();
            var one = new IVsWindowFrame[1];
            while (frames.Next(1, one, out uint fetched) == VSConstants.S_OK && fetched == 1)
            {
                var frame = one[0];
                // A query window's own control is SSMS's editor-and-results control, which is also the code window.
                if (frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out object window) != VSConstants.S_OK || !(window is IVsCodeWindow code)) continue;
                if (code.GetPrimaryView(out IVsTextView view) != VSConstants.S_OK || view == null) continue;
                var buffer = adapters.GetWpfTextView(view)?.TextBuffer;
                if (buffer == null || !buffer.ContentType.IsOfType("SQL")) continue;
                if (frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out object moniker) != VSConstants.S_OK || !(moniker is string key) || key.Length == 0) continue;
                var text = buffer.CurrentSnapshot;
                if (text.Length > MaxLength) continue;

                bool changed = !written.TryGetValue(key, out int version) || version != text.Version.VersionNumber;
                written[key] = text.Version.VersionNumber;
                string connection = "";
                try { connection = Ssms.ConnectionOf(window); }
                catch (Exception e) { Ssms.Trace("no connection noted for " + key + ": " + e.Message); } // the window comes back unconnected
                open.Add(new KeyValuePair<string, SessionWindow>(key, new SessionWindow
                {
                    Text = changed ? text.GetText() : null,
                    Path = IsSavedFile(frame, key) ? key : "",
                    Connection = connection,
                    Active = string.Equals(key, front, StringComparison.OrdinalIgnoreCase),
                }));
            }
            session.Save(open, DateTime.Now);
            if (open.Count != noted) Ssms.Trace("session: " + (noted = open.Count) + " query windows open");
            foreach (var closed in written.Keys.Where(k => open.All(o => !string.Equals(o.Key, k, StringComparison.OrdinalIgnoreCase))).ToList())
                written.Remove(closed);
        }

        /// <summary>True for a window showing a file of the user's that holds exactly the window's text.</summary>
        static bool IsSavedFile(IVsWindowFrame frame, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (IsScratch(path) || !File.Exists(path)) return false;
            frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out object data);
            return data is IVsPersistDocData document && document.IsDocDataDirty(out int dirty) == VSConstants.S_OK && dirty == 0;
        }

        /// <summary>A new query lives in a file in the temp folder until it is saved somewhere.</summary>
        static bool IsScratch(string path)
        {
            try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return true; } // not a file path at all
        }

        static DateTime? StartOf(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId)) return process.StartTime;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
            {
                return null; // no such process, or not one of ours
            }
        }

        /// <summary>
        /// Reopens the windows of every session whose SSMS is gone, or with <paramref name="wanted"/> false
        /// (the option is off) throws those sessions away.
        /// </summary>
        void Reopen(bool wanted)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int reopened = 0, failed = 0;
            var logins = new Dictionary<string, object>(); // what one window learnt about a login, for the windows after it
            foreach (var abandoned in Session.Abandoned(Session.Root, StartOf))
            {
                string folder;
                try { folder = Session.Claim(abandoned); }
                catch (IOException) { continue; } // another SSMS, starting at this moment, has taken it
                var windows = Session.Load(folder);
                Ssms.Trace("session: " + (wanted ? "reopening " : "discarding ") + windows.Count + " windows of " + Path.GetFileName(abandoned));
                if (wanted)
                {
                    int before = failed;
                    foreach (var window in windows.OrderBy(w => w.Active)) // the one that was in front last, so that it is in front again
                    {
                        try
                        {
                            if (Open(window, logins)) reopened++;
                        }
                        catch (Exception e)
                        {
                            failed++;
                            Ssms.Log("could not reopen a query window", e);
                        }
                    }
                    // Left where it is, a session that did not fully reopen is set aside as ".failed" by the next start: its text is not lost.
                    if (failed > before) continue;
                }
                Directory.Delete(folder, true);
            }
            if (reopened > 0 && failed == 0) Ssms.Status("SQLFinish: reopened " + reopened + (reopened == 1 ? " query window." : " query windows."));
        }

        /// <summary>Reopens one window. False for a window with nothing in it, which is not worth bringing back.</summary>
        bool Open(SessionWindow window, Dictionary<string, object> logins)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // A saved file comes back as that file; anything else as a new query holding the text.
            bool saved = window.Path.Length > 0 && !IsScratch(window.Path) && File.Exists(window.Path);
            if (!saved && File.ReadAllText(window.TextFile).Trim().Length == 0) return false;
            var dte = services.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            Ssms.OpenQuery(window.Connection, () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (saved) dte?.ItemOperations.OpenFile(window.Path);
                else Ssms.NewQueryFrom(window.TextFile);
            }, logins);
            return true;
        }
    }
}
