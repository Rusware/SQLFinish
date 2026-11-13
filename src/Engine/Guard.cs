using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLFinish.Engine
{
    /// <summary>What a script is about to do, judged from its text before it runs.</summary>
    public static class Guard
    {
        static readonly HashSet<string> NotAStatement = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FOR", "ON", "AFTER", "OF", "THEN", "GRANT", "DENY", "REVOKE", // FOR UPDATE, ON DELETE CASCADE, triggers, MERGE, permissions
        };

        static readonly HashSet<string> NextStatement = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "DECLARE", "IF", "WHILE", "BEGIN", "END", "ELSE", "EXEC", "EXECUTE",
            "PRINT", "RETURN", "CREATE", "ALTER", "DROP", "TRUNCATE", "COMMIT", "ROLLBACK", "GO", "USE", "THROW", "RAISERROR",
            "WAITFOR", "GRANT", "DENY", "REVOKE",
        };

        static List<Tok> Code(string sql) =>
            Tokenizer.Scan(sql).Where(x => x.Type != TokType.Ws && x.Type != TokType.Comment).ToList();

        /// <summary>True when the script creates, alters, drops or renames something, so cached catalogs go stale.</summary>
        public static bool ChangesSchema(string sql) =>
            Code(sql).Any(t => t.Is("CREATE") || t.Is("ALTER") || t.Is("DROP") || t.Is("sp_rename"));

        /// <summary>
        /// Every UPDATE or DELETE in the script that has no WHERE, as "UPDATE dbo.Orders (line 3)".
        /// Temp tables and table variables are ignored. SQL built in strings is not looked into.
        /// </summary>
        public static List<string> Unfiltered(string sql)
        {
            var found = new List<string>();
            var t = Code(sql);
            for (int i = 0; i < t.Count; i++)
            {
                bool update = t[i].Is("UPDATE");
                if (!update && !t[i].Is("DELETE")) continue;
                if (i > 0 && (t[i - 1].IsP(",") || (t[i - 1].Type == TokType.Word && NotAStatement.Contains(t[i - 1].Text)))) continue;
                if (i + 1 < t.Count && (t[i + 1].IsP("(") || t[i + 1].Is("STATISTICS"))) continue; // UPDATE(col), UPDATE STATISTICS

                // the target: [TOP (n)] [FROM] name[.name...]
                int k = i + 1;
                if (k < t.Count && t[k].Is("TOP")) k = SkipGroup(t, k + 1);
                if (k < t.Count && t[k].Is("FROM")) k++;
                string target = "";
                while (k < t.Count && (t[k].IsName || (t[k].Type == TokType.Word && t[k].Text[0] == '@')))
                {
                    target += t[k].Text;
                    if (k + 1 < t.Count && t[k + 1].IsP(".")) { target += "."; k += 2; } else break;
                }
                if (target.StartsWith("#") || target.StartsWith("@")) continue;

                bool where = false;
                int depth = 0, cases = 0, sets = 0;
                for (int j = i + 1; j < t.Count && !where; j++)
                {
                    var x = t[j];
                    if (x.IsP("(")) depth++;
                    else if (x.IsP(")")) { if (--depth < 0) break; }
                    else if (depth > 0) continue;
                    else if (x.IsP(";")) break;
                    else if (x.Is("CASE")) cases++;
                    else if (cases > 0) { if (x.Is("END")) cases--; }
                    else if (x.Is("WHERE")) where = true;
                    else if (x.Is("SET")) { if (++sets > (update ? 1 : 0)) break; }
                    else if (x.Is("WITH")) { if (!(j + 1 < t.Count && t[j + 1].IsP("("))) break; } // a CTE starts, unlike WITH (hint)
                    else if (x.Type == TokType.Word && NextStatement.Contains(x.Text)) break;
                }
                if (where) continue;

                int line = 1 + sql.Take(t[i].Start).Count(c => c == '\n');
                found.Add((update ? "UPDATE " : "DELETE ") + target + " (line " + line + ")");
            }
            return found;
        }

        /// <summary>Index after the token or parenthesised group at <paramref name="k"/>.</summary>
        static int SkipGroup(List<Tok> t, int k)
        {
            if (k >= t.Count || !t[k].IsP("(")) return k + 1;
            int depth = 0;
            for (; k < t.Count; k++)
            {
                if (t[k].IsP("(")) depth++;
                else if (t[k].IsP(")") && --depth == 0) return k + 1;
            }
            return k;
        }
    }
}
