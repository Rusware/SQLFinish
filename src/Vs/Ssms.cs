using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SQLFinish.Engine;

namespace SQLFinish.Vs
{
    /// <summary>The server a query window is connected to, and a way to open our own connection to it.</summary>
    internal sealed class Connection
    {
        public string Server, Database;
        public Func<string, IDbConnection> Open; // opens a new connection on the given database
    }

    /// <summary>The rows behind a results grid. Rows and columns count from 0.</summary>
    internal sealed class ResultGrid
    {
        public List<XlsxColumn> Columns;
        public long Rows;
        public bool Complete;                // false while the query is still filling the grid
        public Func<long, int, object> Value; // the stored value: a SqlTypes value for most columns, null for NULL
        public Func<long, int, string> Text;  // the same cell as SSMS shows it
    }

    /// <summary>
    /// The only place that touches SSMS's own assemblies. They are not a supported extension API, so they
    /// are reached by reflection: a renamed member costs us schema suggestions or the Excel export, never a
    /// crash or a failed load.
    /// </summary>
    internal static class Ssms
    {
        static Type FindType(string assembly, string type)
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly) ?? Assembly.Load(assembly);
            return asm.GetType(type, throwOnError: true);
        }

        /// <summary>The connection of the active query window, or null when there is none. UI thread only.</summary>
        public static Connection ActiveConnection()
        {
            try
            {
                var factoryProperty = FindType("SqlPackageBase", "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache")
                    .GetProperty("ScriptFactory", BindingFlags.Public | BindingFlags.Static);
                object factory = factoryProperty.GetValue(null);
                if (factory == null) return null;
                object window = factoryProperty.PropertyType.GetProperty("CurrentlyActiveWndConnectionInfo").GetValue(factory);
                object ui = window?.GetType().GetProperty("UIConnectionInfo").GetValue(window);
                if (ui == null) return null;

                var util = FindType("ConnectionDlg", "Microsoft.SqlServer.Management.UI.ConnectionDlg.UIConnectionInfoUtil");
                var arg = new[] { ui.GetType() };
                object core = util.GetMethod("GetCoreConnectionInfo", arg).Invoke(null, new[] { ui });
                var create = core.GetType().GetMethod("CreateConnectionObject", Type.EmptyTypes);
                Trace("window is on " + ui.GetType().GetProperty("ServerName").GetValue(ui) + " / " + util.GetMethod("GetDatabaseName", arg).Invoke(null, new[] { ui }));
                return new Connection
                {
                    Server = (string)ui.GetType().GetProperty("ServerName").GetValue(ui),
                    // The window's current database: SSMS keeps this up to date when it is changed by USE or the toolbar.
                    Database = (string)util.GetMethod("GetDatabaseName", arg).Invoke(null, new[] { ui }),
                    Open = database =>
                    {
                        // Connect straight to the database where possible: Azure SQL Database cannot switch with USE.
                        if (database.Length > 0) core.GetType().GetProperty("DatabaseName")?.SetValue(core, database);
                        var connection = (IDbConnection)create.Invoke(core, null);
                        connection.Open();
                        if (database.Length > 0 && !string.Equals(connection.Database, database, StringComparison.OrdinalIgnoreCase))
                            connection.ChangeDatabase(database);
                        return connection;
                    },
                };
            }
            catch (Exception e)
            {
                Log("could not read the query window's connection", e);
                return null;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetFocus();

        static object GridStorage(System.Windows.Forms.Control control) => control.GetType().GetProperty("GridStorage")?.GetValue(control);

        // Other grids (client statistics, template parameters) have a storage that is not a result set.
        static Type ResultSet(object storage) => storage?.GetType().GetInterface("Microsoft.SqlServer.Management.QueryExecution.IGridResultSet");

        static IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control control)
        {
            foreach (System.Windows.Forms.Control child in control.Controls)
            {
                yield return child;
                foreach (var below in Descendants(child)) yield return below;
            }
        }

        /// <summary>
        /// The storage of the results grid that has the keyboard focus (the one that was right-clicked;
        /// SSMS finds the grid for its own "Save Results As" the same way). With the focus elsewhere in a
        /// query window, as when the shortcut or the toolbar is used while typing, that window's first
        /// results grid. Null when there is neither.
        /// </summary>
        static object ResultStorage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var focus = System.Windows.Forms.Control.FromChildHandle(GetFocus());
            object storage = null;
            for (var control = focus; control != null && storage == null; control = control.Parent)
                storage = GridStorage(control);
            // Not in a grid: look around the focus, nearest container first, so only its own window's grids are found.
            if (storage == null)
                for (var control = focus; control != null && storage == null; control = control.Parent)
                    storage = Descendants(control).Select(GridStorage).FirstOrDefault(s => ResultSet(s) != null);
            // The focus is in none of SSMS's own controls (a menu is open, say): the active query window's first grid.
            if (focus == null && ActiveDocumentView() is System.Windows.Forms.Control window)
                storage = Descendants(window).Select(GridStorage).FirstOrDefault(s => ResultSet(s) != null);
            return ResultSet(storage) == null ? null : storage;
        }

        /// <summary>The active document window's own control (for a query window, SSMS's editor-and-results control), or null.</summary>
        static object ActiveDocumentView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            object frame = null, view = null;
            (Package.GetGlobalService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection)?.GetCurrentElementValue((uint)Microsoft.VisualStudio.VSConstants.VSSELELEMID.SEID_DocumentFrame, out frame);
            (frame as IVsWindowFrame)?.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out view);
            return view;
        }

        /// <summary>
        /// How SSMS itself writes down a query window's connection (server, database, kind of login and user
        /// name; never a password), or "" for a window that has none. <paramref name="window"/> is the
        /// window's own control.
        /// </summary>
        public static string ConnectionOf(object window)
        {
            object info = window.GetType().GetProperty("Connection")?.GetValue(window);
            if (info == null) return "";
            var text = new System.Text.StringBuilder();
            using (var writer = System.Xml.XmlWriter.Create(text, new System.Xml.XmlWriterSettings { OmitXmlDeclaration = true }))
                info.GetType().GetMethod("SaveToStream").Invoke(info, new object[] { writer, true });
            return text.ToString();
        }

        /// <summary>SSMS's own way of making query windows, and the type it describes a connection with.</summary>
        static object ScriptFactory(out MethodInfo createNewScript, out Type connectionInfo)
        {
            var factoryProperty = FindType("SqlPackageBase", "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache")
                .GetProperty("ScriptFactory", BindingFlags.Public | BindingFlags.Static);
            createNewScript = factoryProperty.PropertyType.GetMethods().First(m => m.Name == "CreateNewScript" && m.GetParameters().Length == 3
                && m.GetParameters()[1].ParameterType.Name == "UIConnectionInfo");
            connectionInfo = createNewScript.GetParameters()[1].ParameterType;
            return factoryProperty.GetValue(null) ?? throw new InvalidOperationException("SSMS has no script factory yet");
        }

        /// <summary>Opens a new, unsaved query window holding a copy of <paramref name="file"/>.</summary>
        public static void NewQueryFrom(string file)
        {
            object factory = ScriptFactory(out var create, out _);
            create.Invoke(factory, new object[] { file, null, null });
        }

        /// <summary>
        /// Runs <paramref name="open"/>, which opens one query window, and gives that window the
        /// <paramref name="connection"/> written down by <see cref="ConnectionOf"/>. SSMS then connects as it
        /// does for any window it opens with a known connection: silently where no password is needed, else
        /// through its own dialog, filled in. With "" the window is left unconnected and nothing is asked.
        /// <paramref name="logins"/> carries what one window learnt to the next (keep one for a run of
        /// windows): a password typed for the first window on a server is not asked for again by the others,
        /// and a server that did not answer is not waited for again.
        /// </summary>
        public static void OpenQuery(string connection, Action open, Dictionary<string, object> logins)
        {
            const BindingFlags any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            Func<object, string, object> get = (target, property) => target.GetType().GetProperty(property, any)?.GetValue(target);
            object info = null;
            string login = null;
            try
            {
                if (connection.Length > 0)
                {
                    ScriptFactory(out _, out var infoType);
                    using (var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(connection)))
                    {
                        reader.MoveToContent();
                        info = infoType.GetMethod("LoadFromStream").Invoke(null, new object[] { reader });
                    }
                    login = get(info, "ServerName") + "\n" + get(info, "AuthenticationType") + "\n" + get(info, "UserName");
                    if (logins.TryGetValue(login, out object password))
                    {
                        if (password == null) info = null; // it did not connect a moment ago
                        else info.GetType().GetProperty("InMemoryPassword")?.SetValue(info, password);
                    }
                }
            }
            catch (Exception x)
            {
                info = null;
                Append("ERROR could not read a reopened window's connection: " + x); // it comes back unconnected
            }

            // SSMS connects a new window by itself as the window is created; the only moment to tell it which
            // connection, or not to, is its own (internal) announcement of the new editor, just before that.
            object editor = null;
            EventHandler prepare = (sender, e) =>
            {
                try
                {
                    editor = get(e, "NewEditor");
                    if (editor == null) return;
                    if (info == null) editor.GetType().GetProperty("AutoConnect", any)?.SetValue(editor, false);
                    else editor.GetType().GetMethod("SetConnection", new[] { info.GetType() }).Invoke(editor, new[] { info });
                }
                catch (Exception x)
                {
                    Append("ERROR could not give a reopened window its connection: " + x); // SSMS asks for one instead
                }
            };
            Delegate handler = null;
            EventInfo created = null;
            try
            {
                created = FindType("SQLEditors", "Microsoft.SqlServer.Management.UI.VSIntegration.Editors.ScriptEditorControl")
                    .GetEvent("NewScriptEditor", BindingFlags.NonPublic | BindingFlags.Static);
                handler = Delegate.CreateDelegate(created.EventHandlerType, prepare.Target, prepare.Method);
                created.GetAddMethod(true).Invoke(null, new object[] { handler });
            }
            catch (Exception x)
            {
                handler = null;
                Append("ERROR could not reach SSMS's new query windows: " + x); // the window still opens; SSMS asks for its connection
            }
            try
            {
                open();
            }
            finally
            {
                if (handler != null) created.GetRemoveMethod(true).Invoke(null, new object[] { handler });
            }
            if (info == null || editor == null) return; // nothing to connect, or nothing known about how it went
            try
            {
                if (get(editor, "IsConnected") as bool? == false) logins[login] = null;
                // SSMS's own SecureString, held only until the windows are open
                else if (get(editor, "Connection") is object live && get(live, "InMemoryPassword") is object typed) logins[login] = typed;
            }
            catch (Exception x)
            {
                Append("ERROR could not tell whether a reopened window connected: " + x);
            }
        }

        /// <summary>True when <see cref="Results"/> would find a grid with rows. Cheap: the shell asks often. UI thread only.</summary>
        public static bool HasResults()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                object storage = ResultStorage();
                return storage != null && (long)storage.GetType().GetMethod("NumRows", Type.EmptyTypes).Invoke(storage, null) > 0;
            }
            catch (Exception)
            {
                return false; // said properly, once, when the export itself is tried
            }
        }

        /// <summary>The rows of the grid found by <see cref="ResultStorage"/>, or null. UI thread only.</summary>
        public static ResultGrid Results()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                object storage = ResultStorage();
                var results = ResultSet(storage);
                if (results == null) return null;

                var type = storage.GetType();
                var names = (System.Collections.Specialized.StringCollection)results.GetProperty("ColumnNames").GetValue(storage);
                var schemaRow = results.GetMethod("GetSchemaRow");
                var columns = new List<XlsxColumn>();
                for (int i = 0; i < names.Count; i++)
                {
                    var row = (DataRow)schemaRow.Invoke(storage, new object[] { i });
                    Func<string, object> field = name => row.Table.Columns.Contains(name) && !row.IsNull(name) ? row[name] : null;
                    columns.Add(new XlsxColumn { Name = names[i], SqlType = field("DataTypeName") as string, Scale = Convert.ToInt32(field("NumericScale") ?? 0) });
                }

                // In a grid the storage's column 0 is the row-number column.
                int first = (type.GetProperty("GridMode")?.GetValue(storage) as bool? ?? true) ? 1 : 0;
                var value = (Func<long, int, object>)Delegate.CreateDelegate(typeof(Func<long, int, object>), storage, results.GetMethod("GetCellData"));
                var text = (Func<long, int, string>)Delegate.CreateDelegate(typeof(Func<long, int, string>), storage, results.GetMethod("GetCellDataAsString"));
                return new ResultGrid
                {
                    Columns = columns,
                    Rows = (long)type.GetMethod("NumRows", Type.EmptyTypes).Invoke(storage, null),
                    Complete = type.GetProperty("StoredAllData")?.GetValue(storage) as bool? ?? true,
                    Value = (r, c) => value(r, c + first),
                    Text = (r, c) => text(r, c + first),
                };
            }
            catch (Exception e)
            {
                Log("could not read the results grid", e);
                return null;
            }
        }

        /// <summary>Set the environment variable SQLFINISH_TRACE=1 to log what completion does to %TEMP%\SQLFinish.log.</summary>
        static readonly bool Tracing = Environment.GetEnvironmentVariable("SQLFINISH_TRACE") == "1";

        public static void Trace(string message)
        {
            if (!Tracing) return;
            Append(message);
        }

        static void Append(string line)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SQLFinish.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine); }
            catch (System.IO.IOException) { }
        }

        /// <summary>Records a failure: always in %TEMP%\SQLFinish.log and SSMS's activity log, and briefly in the status bar.</summary>
        public static void Log(string what, Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            ActivityLog.TryLogWarning("SQLFinish", what + ": " + e);
            Append("ERROR " + what + ": " + e);
            Status("SQLFinish " + what + ": " + e?.Message);
        }

        /// <summary>Turns the buffer's text from <paramref name="before"/> into <paramref name="after"/> as one edit (one undo step).</summary>
        public static void ReplaceText(Microsoft.VisualStudio.Text.ITextBuffer buffer, string before, string after)
        {
            int head = 0, tail = 0;
            while (head < before.Length && head < after.Length && before[head] == after[head]) head++;
            while (tail < before.Length - head && tail < after.Length - head && before[before.Length - 1 - tail] == after[after.Length - 1 - tail]) tail++;
            if (head + tail == before.Length && before.Length == after.Length) return;
            buffer.Replace(new Microsoft.VisualStudio.Text.Span(head, before.Length - head - tail), after.Substring(head, after.Length - head - tail));
        }

        /// <summary>
        /// Puts <paramref name="columns"/> over the given span of <paramref name="text"/> (the view's whole
        /// script), lays that statement out again, and leaves the caret after the columns.
        /// </summary>
        public static void InsertColumns(Microsoft.VisualStudio.Text.Editor.ITextView view, string text, int start, int length, string columns)
        {
            ReplaceText(view.TextBuffer, text, Completion.WithColumnsLaidOut(text, start, length, columns, out int caret));
            view.Caret.MoveTo(new Microsoft.VisualStudio.Text.SnapshotPoint(view.TextSnapshot, Math.Min(caret, view.TextSnapshot.Length)));
        }

        /// <summary>Shows text in the status bar, from any thread.</summary>
#pragma warning disable VSSDK007 // fire and forget: nothing depends on the status text having been shown
        public static void Status(string text) =>
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                (Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar)?.SetText(text);
            }).FileAndForget("SQLFinish/Status");
#pragma warning restore VSSDK007
    }

    /// <summary>One catalog snapshot per server and database, loaded off the UI thread on first use.</summary>
    internal static class SchemaCache
    {
        static readonly ConcurrentDictionary<string, Task<Schema>> loads =
            new ConcurrentDictionary<string, Task<Schema>>(StringComparer.OrdinalIgnoreCase);

        public static void Clear() => loads.Clear();

        /// <summary>Forgets the catalogs because a script that changes the schema is about to run.</summary>
        public static void ClearForSchemaChange()
        {
            Clear();
            // ponytail: Execute returns before the script finishes, so forget again a little later.
            // A DDL script that runs longer than this still needs "Refresh Suggestions".
            Task.Delay(5000).ContinueWith(_ => Clear(), TaskScheduler.Default).FileAndForget("SQLFinish/Clear");
        }

        public static async Task<Schema> GetAsync(Connection connection, string useDatabase, CancellationToken token)
        {
            if (connection == null) return Schema.Empty;
            string database = useDatabase ?? connection.Database ?? "";
            string key = connection.Server + "\n" + database;
            var load = loads.GetOrAdd(key, _ => Task.Run(() =>
            {
                using (var cn = connection.Open(database))
                {
                    var schema = Schema.Load(cn);
                    Ssms.Trace("loaded " + cn.Database + ": " + schema.Objects.Count + " objects, " + schema.ForeignKeys.Count + " foreign key columns");
                    return schema;
                }
            }));

            // Never hold the list up long for a slow server: offer keywords now, the catalog on a later keystroke.
            if (await Task.WhenAny(load, Task.Delay(4000, token)).ConfigureAwait(false) != load)
            {
                Ssms.Status("SQLFinish: still reading the catalog of " + database + "; suggestions will fill in shortly.");
                return Schema.Empty;
            }
            try
            {
                return await load.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                loads.TryRemove(key, out _); // retry next time rather than caching the failure
                Ssms.Log("could not read the catalog of " + database, e);
                return Schema.Empty;
            }
        }
    }
}
