using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SQLFinish.Engine
{
    // Declaration order is the order suggestions are listed in.
    public enum ItemKind { Join, Action, Column, Alias, Table, View, Procedure, Function, Schema, Database, Snippet, Keyword }

    public sealed class Item
    {
        public string Text, Insert, Detail;
        public string Filter; // what typing is matched against, when that is not Text
        public ItemKind Kind;
        public override string ToString() => Kind + ":" + Text;
    }

    public enum Mode { None, StatementStart, Tables, Procedures, Databases, JoinOn, Qualified, Expression, AfterOperand }

    /// <summary>A table (or derived table) named in the statement around the caret.</summary>
    public sealed class TableRef
    {
        public List<string> Parts = new List<string>(); // [db.][schema.]name; empty for a subquery or function
        public string Alias;
        public string Via;     // the keyword that introduced it: FROM, JOIN, CROSS (join), APPLY, UPDATE, INTO, USING
        public int Group;     // index of the enclosing "(" token, -1 at statement level
        public int End;        // statement token index just past the reference
        public string Name => Parts.Count > 0 ? Parts[Parts.Count - 1] : null;
        public string SchemaName => Parts.Count > 1 && Parts[Parts.Count - 2].Length > 0 ? Parts[Parts.Count - 2] : null;
    }

    /// <summary>Where the caret is, worked out from the text alone (no database needed).</summary>
    public sealed class Context
    {
        public Mode Mode;
        public int PrefixStart;                           // start of the word being typed
        public bool Auto;                                 // worth opening the list while typing
        public bool OnSpace;                              // worth opening the list straight after a space
        public List<string> Qualifier = new List<string>(); // the "a.b." before the caret
        public List<TableRef> Refs = new List<TableRef>();  // tables visible at the caret
        public TableRef Joined;                           // JoinOn: the table just joined
        public List<Tok> Before = new List<Tok>();        // the statement's tokens ahead of the caret
        public bool WithAlias, IsJoin, IsApply;
    }

    public static class Completion
    {
        /// <summary>The suggestion that opens the column picker instead of inserting text.</summary>
        public const string PickColumnsText = "Pick columns...";

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        static readonly Regex Plain = new Regex(@"^[A-Za-z_#][A-Za-z0-9_@#$]*$");

        public static string Quote(string name) =>
            Plain.IsMatch(name) && !Keywords.Reserved.Contains(name) ? name : "[" + name.Replace("]", "]]") + "]";

        static string Qualified(DbObject o) => (Eq(o.Schema, "dbo") ? "" : Quote(o.Schema) + ".") + Quote(o.Name);

        /// <summary>The database named by the last USE before the caret, or null.</summary>
        public static string DatabaseAt(string sql, int caret)
        {
            string db = null;
            var code = Tokenizer.Scan(sql).Where(t => t.Type != TokType.Ws && t.Type != TokType.Comment && t.End <= caret).ToList();
            for (int i = 0; i + 1 < code.Count; i++)
                if (code[i].Is("USE") && code[i + 1].IsName) db = code[i + 1].Name;
            return db;
        }

        public static Context Analyze(string sql, int caret)
        {
            var ctx = new Context { PrefixStart = caret };
            var all = Tokenizer.Scan(sql);

            foreach (var t in all)
            {
                if (t.Start >= caret) break;
                if (t.Type != TokType.Comment && t.Type != TokType.String) continue;
                bool openEnded = !t.Closed || t.Text.StartsWith("--");
                if (caret < t.End || (caret == t.End && openEnded)) return ctx; // inside a comment or string
            }

            var code = all.Where(t => t.Type != TokType.Ws && t.Type != TokType.Comment).ToList();
            var word = code.FirstOrDefault(t => t.Start < caret && caret <= t.End
                && (t.Type == TokType.Word || (t.Type == TokType.Quoted && !t.Closed)));
            if (word != null)
            {
                if (word.Text[0] == '@') return ctx; // variables: nothing to offer
                ctx.PrefixStart = word.Start;
            }

            // ponytail: statements are split on ";" and GO only, so unterminated neighbours share one scope.
            // Split on statement keywords too if cross-statement aliases ever leak into suggestions.
            int first = 0, last = code.Count;
            for (int i = 0; i < code.Count; i++)
            {
                bool separator = code[i].IsP(";") || code[i].Is("GO");
                if (!separator) continue;
                if (code[i].End <= ctx.PrefixStart) first = i + 1;
                else { last = i; break; }
            }
            var stmt = code.GetRange(first, last - first).Where(t => t != word).ToList();
            var before = stmt.Where(t => t.End <= ctx.PrefixStart).ToList();
            ctx.Before = before;

            // Tables visible at the caret: those in the caret's own parenthesis group or an enclosing one.
            var enclosing = new HashSet<int> { -1 };
            var open = new Stack<int>();
            for (int i = 0; i < before.Count; i++)
            {
                if (before[i].IsP("(")) open.Push(i);
                else if (before[i].IsP(")") && open.Count > 0) open.Pop();
            }
            foreach (int i in open) enclosing.Add(i);
            var refs = ParseRefs(stmt);
            ctx.Refs = refs.Where(r => enclosing.Contains(r.Group)).ToList();

            var p = before.Count > 0 ? before[before.Count - 1] : null;

            if (p != null && p.IsP("."))
            {
                int k = before.Count - 1;
                while (k >= 1 && before[k].IsP(".") && (before[k - 1].IsName || before[k - 1].Type == TokType.Word))
                {
                    ctx.Qualifier.Insert(0, before[k - 1].Name);
                    k -= 2;
                }
                if (ctx.Qualifier.Count == 0) return ctx;
                ctx.Mode = Mode.Qualified;
                ctx.Auto = true;
                return ctx;
            }

            if (p == null || p.Is("BEGIN") || p.Is("GO"))
            {
                ctx.Mode = Mode.StatementStart;
                ctx.Auto = true;
                return ctx;
            }

            string up = p.Upper;
            bool afterTableKeyword = up == "FROM" || up == "JOIN" || up == "APPLY" || up == "INTO" || up == "UPDATE"
                || up == "TABLE" || up == "USING" || (p.IsP(",") && ClauseKeyword(before) == "FROM");
            if (afterTableKeyword)
            {
                ctx.Mode = Mode.Tables;
                ctx.Auto = ctx.OnSpace = true;
                ctx.IsJoin = up == "JOIN";
                ctx.IsApply = up == "APPLY";
                bool deleteFrom = up == "FROM" && before.Count > 1 && before[before.Count - 2].Is("DELETE");
                ctx.WithAlias = (up == "FROM" || up == "JOIN" || p.IsP(",")) && !deleteFrom;
            }
            else if (up == "EXEC" || up == "EXECUTE") { ctx.Mode = Mode.Procedures; ctx.Auto = ctx.OnSpace = true; }
            else if (up == "USE") { ctx.Mode = Mode.Databases; ctx.Auto = ctx.OnSpace = true; }
            else if (up == "AS") ctx.Mode = Mode.None; // an alias is being named
            else if (up == "ON" && (ctx.Joined = refs.FirstOrDefault(r => r.End == before.Count - 1)) != null)
            {
                ctx.Mode = Mode.JoinOn;
                ctx.Auto = ctx.OnSpace = true;
            }
            else if (p.IsName || p.Type == TokType.Number || p.Type == TokType.String || p.IsP(")") || p.Type == TokType.Word && p.Text[0] == '@')
            {
                // After an operand the next word may be an alias, so never pop up uninvited:
                // a list that commits on Enter would turn "FROM Orders o<Enter>" into "FROM Orders ON".
                ctx.Mode = Mode.AfterOperand;
                // Where no alias can follow, only a keyword can, and offering those hijacks nothing:
                var table = refs.FirstOrDefault(r => r.End == before.Count && r.Alias != null);
                if (table != null)
                {
                    // straight after a table that has its alias: the next clause, or the ON of a join
                    ctx.Auto = ctx.OnSpace = true;
                    if (table.Via == "JOIN") ctx.Joined = table;
                }
                else
                {
                    // after an operand of a condition or an ORDER BY / GROUP BY list (not of a select list: a column alias may follow)
                    string clause = ClauseKeyword(before);
                    ctx.Auto = clause == "WHERE" || clause == "ON" || clause == "HAVING" || clause == "BY";
                }
            }
            else
            {
                ctx.Mode = Mode.Expression;
                ctx.Auto = true;
                // A column is what comes next here, so open on the space as FROM does for tables.
                // Not after SELECT: the tables are usually not typed yet, so the list would be all functions.
                ctx.OnSpace = up == "BY" || up == "WHERE" || up == "HAVING" || up == "AND" || up == "OR";
            }
            return ctx;
        }

        /// <summary>The nearest clause keyword before the caret at the caret's own parenthesis depth.</summary>
        static string ClauseKeyword(List<Tok> before)
        {
            int depth = 0;
            for (int i = before.Count - 1; i >= 0; i--)
            {
                var t = before[i];
                if (t.IsP(")")) depth++;
                else if (t.IsP("(")) { if (--depth < 0) return "("; }
                else if (depth == 0 && t.Type == TokType.Word)
                {
                    switch (t.Upper)
                    {
                        case "SELECT": case "FROM": case "WHERE": case "ON": case "JOIN": case "SET": case "BY":
                        case "HAVING": case "VALUES": case "INTO": case "UPDATE": case "DECLARE": case "EXEC": case "EXECUTE":
                            return t.Upper;
                    }
                }
            }
            return null;
        }

        static List<TableRef> ParseRefs(List<Tok> s)
        {
            var refs = new List<TableRef>();
            var open = new Stack<int>();
            for (int i = 0; i < s.Count; i++)
            {
                if (s[i].IsP("(")) { open.Push(i); continue; }
                if (s[i].IsP(")")) { if (open.Count > 0) open.Pop(); continue; }
                if (s[i].Type != TokType.Word) continue;
                string up = s[i].Upper;
                if (up != "FROM" && up != "JOIN" && up != "UPDATE" && up != "INTO" && up != "USING" && up != "APPLY") continue;

                int k = i + 1;
                while (true)
                {
                    var r = ParseRef(s, ref k, callAllowed: up != "INTO");
                    if (r == null) break;
                    r.Group = open.Count > 0 ? open.Peek() : -1;
                    r.End = k;
                    r.Via = up == "JOIN" && i > 0 && s[i - 1].Is("CROSS") ? "CROSS" : up;
                    refs.Add(r);
                    if (up == "FROM" && k < s.Count && s[k].IsP(",")) k++; // FROM a, b
                    else break;
                }
                // a derived table's own tables are parsed when the loop walks into its parentheses
            }
            return refs;
        }

        static int SkipGroup(List<Tok> s, int k)
        {
            int depth = 0;
            for (; k < s.Count; k++)
            {
                if (s[k].IsP("(")) depth++;
                else if (s[k].IsP(")") && --depth == 0) return k + 1;
            }
            return k;
        }

        static TableRef ParseRef(List<Tok> s, ref int k, bool callAllowed)
        {
            var r = new TableRef();
            int start = k;
            if (k < s.Count && s[k].IsP("(")) k = SkipGroup(s, k); // derived table
            else
            {
                while (k < s.Count && s[k].IsName)
                {
                    r.Parts.Add(s[k].Name);
                    k++;
                    if (k + 1 < s.Count && s[k].IsP(".") && s[k + 1].IsName) k++;
                    else if (k + 1 < s.Count && s[k].IsP(".") && s[k + 1].IsP(".")) { r.Parts.Add(""); k += 2; } // db..table
                    else break;
                }
                if (r.Parts.Count == 0) return null;
                if (callAllowed && k < s.Count && s[k].IsP("(")) { k = SkipGroup(s, k); r.Parts.Clear(); } // table function
            }
            if (k == start) return null;
            if (k < s.Count && s[k].Is("AS")) k++;
            if (k < s.Count && s[k].IsName && !(k + 1 < s.Count && (s[k + 1].IsP(".") || s[k + 1].IsP("("))))
            {
                r.Alias = s[k].Name;
                k++;
            }
            return r.Parts.Count == 0 && r.Alias == null ? null : r;
        }

        /// <summary>A short alias from a name's initials (OrderDetails -> od) that is not already in use.</summary>
        public static string AliasFor(string name, IEnumerable<string> taken)
        {
            var initials = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!char.IsLetter(c)) continue;
                bool wordStart = initials.Length == 0 || !char.IsLetter(name[i - 1]) || (char.IsUpper(c) && !char.IsUpper(name[i - 1]));
                if (wordStart) initials.Append(char.ToLowerInvariant(c));
            }
            string stem = initials.Length > 0 ? initials.ToString() : "t";
            var used = new HashSet<string>(taken.Where(t => t != null), StringComparer.OrdinalIgnoreCase);
            string alias = stem;
            for (int n = 2; used.Contains(alias) || Keywords.Reserved.Contains(alias); n++) alias = stem + n;
            return alias;
        }

        /// <summary>A column offered by the column picker.</summary>
        public sealed class ColumnChoice
        {
            public string Text, Detail;
        }

        /// <summary>The columns that can be picked at the caret, and the text they would replace.</summary>
        public sealed class Choices
        {
            public int Start, Length;
            public List<ColumnChoice> Columns = new List<ColumnChoice>();
        }

        /// <summary>
        /// True when <paramref name="b"/> ends in a select-list "*" (or "alias.*") right at the caret:
        /// one that follows SELECT [DISTINCT] [TOP n] or a comma of that list, not a multiplication or COUNT(*).
        /// </summary>
        static bool StarAt(List<Tok> b, int caret, out int first, out string qualifier)
        {
            qualifier = null;
            int star = first = b.Count - 1;
            if (star < 0 || !b[star].IsP("*") || b[star].End != caret) return false;
            if (star >= 2 && b[star - 1].IsP(".") && b[star - 2].IsName)
            {
                qualifier = b[star - 2].Name;
                first = star - 2;
            }

            var p = first > 0 ? b[first - 1] : null;
            if (p == null) return false;
            bool afterTop = first > 1 && p.Type == TokType.Number && b[first - 2].Is("TOP");
            if (p.IsP(")"))
            {
                int depth = 0, k = first - 1;
                for (; k >= 0; k--)
                {
                    if (b[k].IsP(")")) depth++;
                    else if (b[k].IsP("(") && --depth == 0) break;
                }
                afterTop = k > 0 && b[k - 1].Is("TOP");
            }
            return p.Is("SELECT") || p.Is("DISTINCT") || p.Is("ALL") || p.Is("PERCENT") || afterTop
                || (p.IsP(",") && ClauseKeyword(b.GetRange(0, first)) == "SELECT");
        }

        /// <summary>
        /// The columns of the tables in scope at the caret, written as they should be inserted, plus the
        /// text an insertion replaces: a "*" or "alias.*" or "alias." just before the caret, else the word
        /// being typed. Null when no known table is in scope.
        /// </summary>
        public static Choices ColumnChoices(string sql, int caret, Schema schema)
        {
            var ctx = Analyze(sql, caret);
            var b = ctx.Before;
            var result = new Choices { Start = ctx.PrefixStart };
            string qualifier = null;
            if (StarAt(b, caret, out int first, out qualifier)) result.Start = b[first].Start;
            else if (ctx.Mode == Mode.Qualified && ctx.Qualifier.Count == 1)
            {
                qualifier = ctx.Qualifier[0];
                result.Start = b[b.Count - 2].Start;
            }
            else if (ctx.Mode == Mode.None || ctx.Mode == Mode.Qualified) return null;
            result.Length = caret - result.Start;

            var tables = ctx.Refs.Select(r => new { Ref = r, Object = r.Name == null ? null : schema.Find(r.SchemaName, r.Name) })
                                 .Where(t => t.Object != null).ToList();
            if (qualifier != null)
                tables = tables.Where(t => Eq(t.Ref.Alias, qualifier) || (t.Ref.Alias == null && Eq(t.Ref.Name, qualifier))).Take(1).ToList();
            if (tables.Count == 0) return null;

            bool qualify = qualifier != null || tables.Count > 1 || tables[0].Ref.Alias != null;
            foreach (var t in tables)
                foreach (var c in t.Object.Columns)
                    result.Columns.Add(new ColumnChoice
                    {
                        Text = (qualify ? Quote(t.Ref.Alias ?? t.Ref.Name) + "." : "") + Quote(c.Name),
                        Detail = c.Type + "  (" + t.Object.Schema + "." + t.Object.Name + ")",
                    });
            return result.Columns.Count == 0 ? null : result;
        }

        /// <summary>A replacement of text in the script.</summary>
        public sealed class Edit
        {
            public int Start, Length;
            public string Text;
        }

        /// <summary>
        /// The script after <paramref name="columns"/> replaced the text at <paramref name="start"/>, with that
        /// statement laid out again (select columns one per line). Falls back to the plain insertion if the
        /// formatter declines. <paramref name="caret"/> is where the inserted columns end in the result.
        /// </summary>
        public static string WithColumnsLaidOut(string text, int start, int length, string columns, out int caret)
        {
            string inserted = text.Remove(start, length).Insert(start, columns);
            caret = start + columns.Length;
            string result;
            try { result = Formatter.FormatStatementAt(inserted, start); }
            catch (InvalidOperationException) { return inserted; }
            // Formatting only moves whitespace, so the caret belongs after the same number of other characters.
            int solid = inserted.Take(caret).Count(c => !char.IsWhiteSpace(c));
            for (caret = 0; solid > 0 && caret < result.Length; caret++)
                if (!char.IsWhiteSpace(result[caret])) solid--;
            return result;
        }

        /// <summary>
        /// With the caret straight after a select-list "*" (or "alias.*"), the column list to put in its
        /// place; null anywhere else, or when the tables behind it are not known.
        /// </summary>
        public static Edit ExpandStar(string sql, int caret, Schema schema)
        {
            if (!StarAt(Analyze(sql, caret).Before, caret, out _, out _)) return null;
            var c = ColumnChoices(sql, caret, schema);
            return c == null ? null : new Edit { Start = c.Start, Length = c.Length, Text = string.Join(", ", c.Columns.Select(x => x.Text)) };
        }

        public static List<Item> Items(Context ctx, Schema schema)
        {
            var items = new List<Item>();
            Action<ItemKind, string, string, string> add = (kind, text, insert, detail) =>
                items.Add(new Item { Kind = kind, Text = text, Insert = insert ?? text, Detail = detail });

            // Each visible table with the name its columns are qualified by.
            var scope = ctx.Refs.Select(r => new
            {
                Ref = r,
                Object = r.Name == null ? null : schema.Find(r.SchemaName, r.Name),
                Qualifier = Quote(r.Alias ?? r.Name ?? ""),
            }).ToList();
            var taken = ctx.Refs.SelectMany(r => new[] { r.Alias, r.Name }).ToList();

            Action<DbObject, string> columns = (o, qualifier) =>
            {
                foreach (var c in o.Columns)
                {
                    add(ItemKind.Column, (qualifier == null ? "" : qualifier + ".") + Quote(c.Name), null, c.Type + "  (" + o.Schema + "." + o.Name + ")");
                    // Matched on the column's own name: typing "tot" must find o.Total ahead of keywords, not after them.
                    if (qualifier != null) items[items.Count - 1].Filter = Quote(c.Name);
                }
            };
            Action keywords = () => { foreach (var k in Keywords.Reserved.OrderBy(k => k)) add(ItemKind.Keyword, k, null, "keyword"); };
            Action<IEnumerable<DbObject>, bool, bool> objects = (list, qualify, alias) =>
            {
                foreach (var o in list)
                {
                    string name = qualify ? Qualified(o) : Quote(o.Name);
                    var kind = o.Kind == 'U' ? ItemKind.Table : o.Kind == 'V' ? ItemKind.View : o.Kind == 'P' ? ItemKind.Procedure : ItemKind.Function;
                    string insert = name + (alias && o.IsTableLike ? " " + AliasFor(o.Name, taken) : o.Kind == 'T' ? "(" : "");
                    add(kind, name, insert, kind.ToString().ToLowerInvariant() + " " + o.Schema + "." + o.Name);
                }
            };
            // Foreign keys between a table and the tables in scope, as ready-made ON conditions.
            Func<DbObject, string, IEnumerable<KeyValuePair<DbObject, string>>> joins = (target, targetQualifier) =>
            {
                var found = new List<KeyValuePair<DbObject, string>>();
                foreach (var fk in schema.ForeignKeys.GroupBy(f => f.Name + "\n" + f.FromSchema + "." + f.FromTable))
                {
                    var f0 = fk.First();
                    var from = schema.Find(f0.FromSchema, f0.FromTable);
                    var to = schema.Find(f0.ToSchema, f0.ToTable);
                    if (from == null || to == null) continue;
                    foreach (var s in scope.Where(x => x.Object != null && x.Ref != ctx.Joined))
                    {
                        DbObject other = s.Object == from ? to : s.Object == to ? from : null;
                        if (other == null || (target != null && other != target)) continue;
                        string q = targetQualifier ?? AliasFor(other.Name, taken);
                        string fromQ = s.Object == from ? s.Qualifier : q, toQ = s.Object == from ? q : s.Qualifier;
                        string condition = string.Join(" AND ", fk.Select(c => fromQ + "." + Quote(c.FromColumn) + " = " + toQ + "." + Quote(c.ToColumn)));
                        found.Add(new KeyValuePair<DbObject, string>(other, target != null ? condition : Qualified(other) + " " + q + " ON " + condition));
                    }
                }
                return found;
            };
            // ON conditions (and what each is based on) joining a table to those in scope: from foreign keys,
            // else guessed from column names, as plenty of databases declare no foreign keys.
            // ponytail: two naming habits only (Customers.Id = Orders.CustomerId, and a shared name ending in "id");
            // plurals are singular + "s". Add habits here if a schema's joins are not being offered.
            Func<DbObject, string, List<KeyValuePair<string, string>>> conditions = (target, targetQualifier) =>
            {
                var found = joins(target, targetQualifier).Select(j => new KeyValuePair<string, string>(j.Value, "foreign key")).ToList();
                if (found.Count > 0) return found;
                Func<string, string> bare = n => n.Replace("_", "").ToLowerInvariant();
                Func<DbObject, string> keyName = o => bare(o.Name.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? o.Name.Substring(0, o.Name.Length - 1) : o.Name) + "id";
                Action<string, string> offer = (left, right) => found.Add(new KeyValuePair<string, string>(left + " = " + right, "matching column names"));
                foreach (var s in scope.Where(x => x.Object != null && x.Ref != ctx.Joined))
                {
                    var targetId = target.Columns.FirstOrDefault(c => bare(c.Name) == "id");
                    var otherId = s.Object.Columns.FirstOrDefault(c => bare(c.Name) == "id");
                    foreach (var c in s.Object.Columns.Where(c => targetId != null && (bare(c.Name) == keyName(target) || bare(c.Name) == bare(target.Name) + "id")))
                        offer(targetQualifier + "." + Quote(targetId.Name), s.Qualifier + "." + Quote(c.Name));
                    foreach (var c in target.Columns.Where(c => otherId != null && (bare(c.Name) == keyName(s.Object) || bare(c.Name) == bare(s.Object.Name) + "id")))
                        offer(targetQualifier + "." + Quote(c.Name), s.Qualifier + "." + Quote(otherId.Name));
                    foreach (var c in target.Columns.Where(c => bare(c.Name) != "id" && bare(c.Name).EndsWith("id")))
                        foreach (var d in s.Object.Columns.Where(d => bare(d.Name) == bare(c.Name)))
                            offer(targetQualifier + "." + Quote(c.Name), s.Qualifier + "." + Quote(d.Name));
                }
                return found;
            };

            switch (ctx.Mode)
            {
                case Mode.Qualified:
                    string q1 = ctx.Qualifier[ctx.Qualifier.Count - 1];
                    if (ctx.Qualifier.Count == 1)
                    {
                        var hit = scope.FirstOrDefault(s => Eq(s.Ref.Alias, q1)) ?? scope.FirstOrDefault(s => s.Ref.Alias == null && s.Object != null && Eq(s.Ref.Name, q1));
                        if (hit != null) { if (hit.Object != null) columns(hit.Object, null); }
                        else if (schema.HasSchema(q1)) objects(schema.Objects.Where(o => Eq(o.Schema, q1)), false, false);
                    }
                    else
                    {
                        var o = schema.Find(ctx.Qualifier[ctx.Qualifier.Count - 2], q1);
                        if (o != null) columns(o, null);
                    }
                    break;

                case Mode.StatementStart:
                    foreach (var s in Keywords.Snippets) add(ItemKind.Snippet, s[0], s[1], s[1]);
                    keywords();
                    break;

                case Mode.Tables:
                    if (ctx.IsApply) { objects(schema.Objects.Where(o => o.Kind == 'T'), true, false); break; }
                    if (ctx.IsJoin)
                        foreach (var j in joins(null, null)) add(ItemKind.Join, j.Value, null, "join on foreign key");
                    objects(schema.Objects.Where(o => o.IsTableLike), true, ctx.WithAlias);
                    foreach (var s in schema.Objects.Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase))
                        add(ItemKind.Schema, Quote(s), null, "schema");
                    break;

                case Mode.Procedures:
                    objects(schema.Objects.Where(o => o.Kind == 'P'), true, false);
                    break;

                case Mode.Databases:
                    foreach (var d in schema.Databases) add(ItemKind.Database, Quote(d), null, "database");
                    break;

                case Mode.JoinOn:
                case Mode.Expression:
                    if (ctx.Mode == Mode.JoinOn)
                    {
                        var joined = scope.FirstOrDefault(s => s.Ref == ctx.Joined);
                        if (joined?.Object != null)
                            foreach (var c in conditions(joined.Object, joined.Qualifier)) add(ItemKind.Join, c.Key, null, c.Value);
                    }
                    bool qualify = scope.Count(s => s.Object != null) > 1;
                    if (scope.Any(s => s.Object != null && s.Object.Columns.Count > 0))
                        add(ItemKind.Action, PickColumnsText, "", "Tick several columns and insert them together");
                    foreach (var s in scope.Where(s => s.Object != null)) columns(s.Object, qualify ? s.Qualifier : null);
                    foreach (var s in scope.Where(s => s.Qualifier.Length > 0))
                        add(ItemKind.Alias, s.Qualifier, null, s.Object == null ? "table" : s.Object.Schema + "." + s.Object.Name);
                    foreach (var f in Keywords.Functions) add(ItemKind.Function, f, f + "(", "function");
                    keywords();
                    break;

                case Mode.AfterOperand:
                    if (ctx.Joined != null)
                    {
                        // a table was just joined: it needs its condition next
                        var joined = scope.FirstOrDefault(s => s.Ref == ctx.Joined);
                        if (joined?.Object != null)
                            foreach (var c in conditions(joined.Object, joined.Qualifier)) add(ItemKind.Join, "ON " + c.Key, null, c.Value);
                        add(ItemKind.Snippet, "ON", null, "clause");
                    }
                    else foreach (var c in Keywords.Clauses) add(ItemKind.Snippet, c, null, "clause");
                    keywords();
                    break;
            }

            // one entry per name: the first (highest-priority) wins
            return items.GroupBy(i => i.Text, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                        .OrderBy(i => i.Kind).ToList();
        }
    }
}
