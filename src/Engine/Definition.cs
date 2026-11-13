using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace SQLFinish.Engine
{
    /// <summary>The source of a procedure, view, function or trigger, as the database holds it.</summary>
    public sealed class Module
    {
        public string Database, Schema, Name;
        public string Text; // null when there is none to read: a table, an encrypted module, or no permission
        public bool AnsiNulls, QuotedIdentifier;
    }

    /// <summary>Finds the object named at the caret and turns its source into a script that modifies it.</summary>
    public static class Definition
    {
        /// <summary>
        /// The parts of the name the caret is in or touching: [name], [schema, name] or [database, schema, name]
        /// (schema "" for database..name). Null when the caret is not in a name.
        /// </summary>
        public static List<string> NameAt(string sql, int caret)
        {
            var code = Tokenizer.Scan(sql).Where(t => t.Type != TokType.Ws && t.Type != TokType.Comment).ToList();
            int first = code.FindIndex(t => t.IsName && t.Start <= caret && caret <= t.End), last = first;
            if (first < 0) return null;
            var parts = new List<string> { code[first].Name };
            while (first >= 2 && code[first - 1].IsP("."))
            {
                if (code[first - 2].IsName) { parts.Insert(0, code[first - 2].Name); first -= 2; }
                else if (first >= 3 && code[first - 2].IsP(".") && code[first - 3].IsName) { parts.InsertRange(0, new[] { code[first - 3].Name, "" }); first -= 3; }
                else break;
            }
            while (last + 2 < code.Count && code[last + 1].IsP("."))
            {
                if (code[last + 2].IsName) { parts.Add(code[last + 2].Name); last += 2; }
                else if (last + 3 < code.Count && code[last + 2].IsP(".") && code[last + 3].IsName) { parts.AddRange(new[] { "", code[last + 3].Name }); last += 3; }
                else break;
            }
            return parts.Count <= 3 ? parts : null; // four parts is a linked server: not ours to open
        }

        static string Bracket(string name) => "[" + name.Replace("]", "]]") + "]";

        /// <summary>Reads the named object from the database <paramref name="cn"/> is on. Null when there is no such object.</summary>
        public static Module Load(IDbConnection cn, List<string> parts)
        {
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT DB_NAME(), SCHEMA_NAME(o.schema_id), o.name, m.definition, m.uses_ansi_nulls, m.uses_quoted_identifier
FROM sys.objects o LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE o.object_id = OBJECT_ID(@name)";
                var name = cmd.CreateParameter();
                name.ParameterName = "@name";
                name.DbType = DbType.String;
                name.Value = string.Join(".", parts.Select(p => p.Length == 0 ? "" : Bracket(p)));
                cmd.Parameters.Add(name);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return new Module
                    {
                        Database = r.GetString(0), Schema = r.GetString(1), Name = r.GetString(2),
                        Text = r.IsDBNull(3) ? null : r.GetString(3),
                        AnsiNulls = !r.IsDBNull(4) && r.GetBoolean(4), QuotedIdentifier = !r.IsDBNull(5) && r.GetBoolean(5),
                    };
                }
            }
        }

        /// <summary>
        /// The script that changes <paramref name="m"/>: its source with CREATE turned into ALTER, on its own
        /// database, under the ANSI_NULLS and QUOTED_IDENTIFIER settings it was created with (altering under
        /// other settings would silently change how it runs).
        /// </summary>
        // ponytail: the source is used as stored, so an object renamed with sp_rename still carries its old name
        // here (SQL Server never rewrites it). Patch the name from Schema/Name if that ever bites.
        public static string ModifyScript(Module m)
        {
            string text = m.Text;
            var code = Tokenizer.Scan(text).Where(t => t.Type != TokType.Ws && t.Type != TokType.Comment).Take(2).ToList();
            if (code.Count == 2 && code[0].Is("CREATE") && !code[1].Is("OR")) // "CREATE OR ALTER" already modifies
                text = text.Remove(code[0].Start, code[0].Text.Length).Insert(code[0].Start, "ALTER");
            return "USE " + Bracket(m.Database) + "\r\nGO\r\n"
                + "SET ANSI_NULLS " + (m.AnsiNulls ? "ON" : "OFF") + "\r\nGO\r\n"
                + "SET QUOTED_IDENTIFIER " + (m.QuotedIdentifier ? "ON" : "OFF") + "\r\nGO\r\n"
                + text.TrimEnd() + "\r\nGO\r\n";
        }
    }
}
