using System;
using System.Collections.Concurrent;
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

    /// <summary>
    /// The only place that touches SSMS's own assemblies. They are not a supported extension API, so they
    /// are reached by reflection: a renamed member costs us schema suggestions, never a crash or a failed load.
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
