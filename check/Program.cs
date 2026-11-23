// Engine self-check: dotnet run --project check
// Exits non-zero if the formatter, completion or the live catalog query misbehave.
using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using SQLFinish.Engine;

static class Program
{
    static int failures;

    static void Check(bool ok, string what)
    {
        if (ok) return;
        failures++;
        Console.WriteLine("FAIL: " + what);
    }

    static void Same(string expected, string actual, string what)
    {
        expected = expected.Replace("\r\n", "\n");
        actual = actual.Replace("\r\n", "\n");
        Check(expected == actual, what + "\n--- expected\n" + expected + "\n--- actual\n" + actual);
    }

    static int Main()
    {
        FormatterChecks();
        CompletionChecks();
        XlsxChecks();
        SessionChecks();
        LiveCatalogCheck();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    static void FormatterChecks()
    {
        Same(@"SELECT TOP (10)
    o.id,
    c.name,
    count(*) AS n
FROM orders o
INNER JOIN customers c
    ON c.id = o.customer_id
        AND c.active = 1
LEFT OUTER JOIN x
    ON x.id = o.id
WHERE o.total > -1
    AND o.d BETWEEN 1 AND 2
GROUP BY o.id, c.name
HAVING count(*) > 1",
            Formatter.Format("select top (10) o.id, c.name, count(*) as n from orders o inner join customers c on c.id=o.customer_id and c.active=1 left outer join x on x.id=o.id where o.total>-1 and o.d between 1 and 2 group by o.id,c.name having count(*)>1"),
            "select with joins");

        Same(@"SELECT
    a,
    CASE
        WHEN x = 1 THEN 'one'
        ELSE 'many'
    END AS label,
    LEFT(name, 3)
FROM t
WHERE id IN (
    SELECT id
    FROM u
)",
            Formatter.Format("select a, case when x=1 then 'one' else 'many' end as label, left(name,3) from t where id in (select id from u)"),
            "case, function call, subquery");

        Same(@"IF @a = 1
BEGIN
    UPDATE t
    SET
        a = 1,
        b = b + 1
    WHERE id = 5;
    DELETE FROM t
    WHERE id = 6
END
ELSE
BEGIN
    INSERT INTO t (a, b)
    VALUES (1, 2),
        (3, 4)
END",
            Formatter.Format("if @a=1 begin update t set a=1,b=b+1 where id=5; delete from t where id=6 end else begin insert into t(a,b) values(1,2),(3,4) end"),
            "blocks and dml");

        Same(@"CREATE TABLE dbo.t (
    id int IDENTITY(1, 1) NOT NULL PRIMARY KEY,
    amount decimal(10, 2) DEFAULT (0)
)",
            Formatter.Format("create table dbo.t(id int identity(1,1) not null primary key, amount decimal(10,2) default(0))"),
            "create table");

        // Formatting one statement leaves its neighbours alone and keeps its indentation.
        Same("select 1;\nSELECT\n    a,\n    b\nFROM t\nWHERE x = 1;\nselect 2",
            Formatter.FormatStatementAt("select 1;\nselect a,b from t where x=1;\nselect 2", 12), "only the statement at the position is formatted");
        Same("select 1;\n    SELECT\n        a,\n        b\n    FROM t;\nselect 2",
            Formatter.FormatStatementAt("select 1;\n    select a,b from t;\nselect 2", 16), "an indented statement keeps its indentation");
        Same("select 1\nGO\nSELECT\n    a,\n    'x\ny'\nFROM t\nGO\nselect 2",
            Formatter.FormatStatementAt("select 1\nGO\nselect a,'x\ny' from t\nGO\nselect 2", 14), "GO separates statements and strings keep their line breaks");

        // Comments and strings must survive untouched, wherever they sit.
        Same("-- header\nSELECT\n    a, -- first\n    b /* second */\nFROM t -- tail\nWHERE x = 'it''s  from  where' -- done",
            Formatter.Format("-- header\nselect a, -- first\n b /* second */ from t -- tail\nwhere x = 'it''s  from  where' -- done"),
            "comments and strings");
        Same("SELECT\n    a -- note\n    ,\n    b\nFROM t", Formatter.Format("select a -- note\n, b from t"), "a comma after a line comment stays out of the comment");

        // Whatever the input, formatting again must change nothing, and must never throw on odd input.
        string[] scripts =
        {
            "with c as (select id from t) select * from c; begin tran; exec dbo.p @a=1,@b='x'; commit",
            "declare @a int=1,@b varchar(10)='x'\nset nocount on\n\nselect @a",
            "begin try insert into t select a,b from u end try begin catch throw; end catch",
            "select distinct a from t union all select b from u order by 1",
            "merge into t using s on t.id=s.id when matched then update set t.a=s.a when not matched then insert(id,a) values(s.id,s.a);",
            "create procedure dbo.p @a int as begin set nocount on; select * from t where a=@a end\ngo\nalter table t add c int null",
            "select [from], \"where\" from [select] where a = N'x' and b <> 0x1F and c >= 1.5e3",
            "select ((a+b)*-c) from t where (a=1 or b=2) and not exists(select 1 from u where u.id = t.id)",
            "select 'unterminated from t",
            "select a from t where b in (((",
            ")) select",
            "",
        };
        foreach (var s in scripts)
        {
            try
            {
                string once = Formatter.Format(s);
                Same(once, Formatter.Format(once), "idempotent: " + s);
            }
            catch (Exception e) { Check(false, "threw on: " + s + "\n" + e); }
        }
    }

    static Schema Shop()
    {
        var s = new Schema();
        Func<string, string, char, string[], DbObject> obj = (schema, name, kind, cols) =>
        {
            var o = new DbObject { Schema = schema, Name = name, Kind = kind, Columns = cols.Select(c => new Column { Name = c, Type = "int" }).ToList() };
            s.Objects.Add(o);
            return o;
        };
        obj("dbo", "Orders", 'U', new[] { "Id", "CustomerId", "Total" });
        obj("dbo", "Customers", 'U', new[] { "Id", "Name" });
        obj("Sales", "OrderDetails", 'U', new[] { "OrderId", "Qty", "Order Note" });
        obj("dbo", "GetOrders", 'P', new string[0]);
        obj("dbo", "Notes", 'U', new[] { "Id", "Order_Id", "Body" });          // no foreign keys declared on these two
        obj("dbo", "Shipments", 'U', new[] { "ShipmentId", "CustomerId" });
        s.ForeignKeys.Add(new ForeignKey { Name = "FK_O_C", FromSchema = "dbo", FromTable = "Orders", FromColumn = "CustomerId", ToSchema = "dbo", ToTable = "Customers", ToColumn = "Id" });
        s.ForeignKeys.Add(new ForeignKey { Name = "FK_OD_O", FromSchema = "Sales", FromTable = "OrderDetails", FromColumn = "OrderId", ToSchema = "dbo", ToTable = "Orders", ToColumn = "Id" });
        s.Databases.AddRange(new[] { "master", "Shop" });
        return s;
    }

    /// <summary>Completes at the "|" in <paramref name="sql"/>.</summary>
    static List<Item> At(string sql, out Context ctx)
    {
        int caret = sql.IndexOf('|');
        ctx = Completion.Analyze(sql.Remove(caret, 1), caret);
        return Completion.Items(ctx, Shop());
    }

    static void CompletionChecks()
    {
        Context c;
        Func<List<Item>, string, Item> find = (list, text) => list.FirstOrDefault(i => i.Text == text);

        var items = At("SELECT * FROM |", out c);
        Check(c.Mode == Mode.Tables && c.OnSpace && c.Auto, "FROM opens the table list");
        Check(find(items, "Orders")?.Insert == "Orders o", "table inserts with an alias");
        Check(find(items, "Sales.OrderDetails")?.Insert == "Sales.OrderDetails od", "non-dbo table is schema-qualified");
        Check(find(items, "GetOrders") == null, "procedures are not offered as tables");

        items = At("DELETE FROM |", out c);
        Check(find(items, "Orders")?.Insert == "Orders", "DELETE FROM adds no alias");

        items = At("SELECT * FROM Orders o JOIN |", out c);
        Check(items.Count > 0 && items[0].Kind == ItemKind.Join, "JOIN lists foreign-key joins first");
        Check(find(items, "Customers c ON o.CustomerId = c.Id") != null, "join to the referenced table");
        Check(find(items, "Sales.OrderDetails od ON od.OrderId = o.Id") != null, "join from the referencing table");

        items = At("SELECT * FROM Orders o JOIN Customers c ON |", out c);
        Check(c.Mode == Mode.JoinOn && items[0].Text == "o.CustomerId = c.Id", "ON offers the foreign-key condition first");

        items = At("SELECT o.| FROM Orders o", out c);
        Check(string.Join(",", items.Select(i => i.Text)) == "Id,CustomerId,Total", "alias. lists that table's columns only");

        items = At("SELECT * FROM Sales.|", out c);
        Check(items.Count == 1 && items[0].Text == "OrderDetails", "schema. lists that schema's objects");

        items = At("SELECT od.| FROM Sales.OrderDetails od", out c);
        Check(find(items, "[Order Note]") != null, "odd column names are bracketed");

        items = At("SELECT | FROM Orders o JOIN Customers c ON c.Id = o.CustomerId", out c);
        Check(find(items, "o.Total") != null && find(items, "c.Name") != null, "columns are alias-qualified when several tables are in scope");

        items = At("SELECT * FROM Orders WHERE Tot|", out c);
        Check(c.Mode == Mode.Expression && c.PrefixStart == 27 && find(items, "Total") != null, "single table: plain column names");

        items = At("SELECT | FROM Orders o WHERE o.Id IN (SELECT 1 FROM Customers c)", out c);
        Check(find(items, "Total") != null && find(items, "c.Name") == null && find(items, "Name") == null, "a subquery's tables stay inside it");
        items = At("SELECT 1 FROM Orders o WHERE o.Id IN (SELECT | FROM Customers c)", out c);
        Check(find(items, "o.Total") != null && find(items, "c.Name") != null, "a subquery sees the outer tables");

        items = At("SELECT 1; SELECT | FROM Customers", out c);
        Check(find(items, "Name") != null && find(items, "Total") == null, "statements are scoped by ;");

        // a forgotten ";" is forgiven: a blank line starts a new statement too
        items = At("SELECT * FROM Orders o\n\nSELECT | FROM Customers", out c);
        Check(find(items, "Name") != null && find(items, "Total") == null && find(items, "o.Total") == null, "a blank line scopes the statement after it");
        items = At("SELECT | FROM Orders\r\n  \r\nSELECT * FROM Customers c", out c);
        Check(find(items, "Total") != null && find(items, "c.Name") == null && find(items, "Name") == null, "and the statement before it");
        items = At("SELECT * FROM Orders o\n\nss|", out c);
        Check(c.Mode == Mode.StatementStart && find(items, "ssf") != null, "typing after a blank line starts a statement");
        At("SELECT * FROM Orders o\n\n|", out c);
        Check(c.Mode == Mode.StatementStart && !c.OnSpace, "an empty line after a blank line is a statement start");
        At("SELECT * FROM Orders o\n-- note\n\n|\n\nSELECT 2", out c);
        Check(c.Mode == Mode.StatementStart, "also between two statements, after a comment");
        At("SELECT * FROM Orders o\n|", out c);
        Check(c.Mode == Mode.AfterOperand, "the very next line still continues the statement");
        items = At("SELECT o.Id\n\nFROM Orders o\n\nWHERE |", out c);
        Check(find(items, "Total") != null, "a blank line before the next clause is not the end");
        items = At("SELECT\n    o.Id,\n\n    | c.Name\nFROM Orders o JOIN Customers c ON c.Id = o.CustomerId", out c);
        Check(find(items, "o.Total") != null && find(items, "c.Name") != null, "nor is one after a comma");
        items = At("SELECT * FROM Orders o WHERE o.Id IN (\n\nSELECT | FROM Customers c\n\n)", out c);
        Check(find(items, "o.Total") != null && find(items, "c.Name") != null, "nor one inside parentheses");
        Tokenizer.BlankLineEndsStatement = false;
        items = At("SELECT * FROM Orders o\n\nSELECT | FROM Customers c", out c);
        Check(find(items, "o.Total") != null, "blank lines mean nothing when the option is off");
        Tokenizer.BlankLineEndsStatement = true;
        Same("select a,b from t\n\nSELECT\n    a,\n    b\nFROM u\n\nselect 3",
            Formatter.FormatStatementAt("select a,b from t\n\nselect a,b from u\n\nselect 3", 22), "only the statement between the blank lines is formatted");

        // a column comes next, so the list opens on the space, as it does for tables after FROM
        foreach (var clause in new[] { "ORDER BY", "GROUP BY", "WHERE", "WHERE o.Id = 1 AND", "WHERE o.Id = 1 OR", "GROUP BY o.Id HAVING" })
        {
            items = At("SELECT * FROM Orders o " + clause + " |", out c);
            Check(c.Mode == Mode.Expression && c.OnSpace && find(items, "Total") != null, clause + " opens the column list on the space");
        }
        At("SELECT |", out c);
        Check(!c.OnSpace, "SELECT does not open the list on the space");

        At("SELECT * FROM Orders o|", out c);
        Check(c.Mode == Mode.AfterOperand && !c.Auto, "no popup while an alias is being typed");

        // once the table has its alias, the next clause is offered
        items = At("SELECT * FROM Orders o |", out c);
        Check(c.Auto && c.OnSpace && items[0].Text == "INNER JOIN" && find(items, "LEFT JOIN") != null && find(items, "WHERE") != null && find(items, "ORDER BY") != null,
            "after an aliased table the join kinds and WHERE are offered");
        items = At("SELECT * FROM Orders o\nwh|", out c);
        Check(c.Auto && items.First(i => i.Text.StartsWith("WH")).Text == "WHERE", "typing the next clause finds it, WHERE ahead of WHEN");
        At("SELECT * FROM Orders |", out c);
        Check(!c.Auto && !c.OnSpace, "nothing by itself after a table with no alias yet");
        At("SELECT o.Id | FROM Orders o", out c);
        Check(!c.Auto, "nothing by itself after a select column: an alias may follow");
        items = At("SELECT * FROM Orders o JOIN Customers c |", out c);
        Check(c.OnSpace && items[0].Text == "ON o.CustomerId = c.Id" && find(items, "ON") != null && find(items, "LEFT JOIN") == null, "a joined table is offered its ON condition");
        items = At("SELECT * FROM Orders o CROSS JOIN Customers c |", out c);
        Check(items[0].Text == "INNER JOIN", "a cross join needs no ON");
        items = At("SELECT * FROM Orders o JOIN Customers c ON c.Id = o.CustomerId |", out c);
        Check(c.Auto && !c.OnSpace && find(items, "LEFT JOIN") != null && find(items, "WHERE") != null, "after a join condition the next clause is offered while typing");
        items = At("SELECT * FROM Orders o WHERE o.Total > 5 |", out c);
        Check(c.Auto && !c.OnSpace && find(items, "ORDER BY") != null && find(items, "AND") != null, "after a WHERE condition AND / ORDER BY are offered while typing");

        items = At("SELECT * FROM Orders o JOIN Customers c ON c.Id = o.CustomerId WHERE |", out c);
        Check(find(items, "o.Total")?.Filter == "Total" && find(items, "WHERE")?.Filter == null, "alias-qualified columns are matched by their own name");

        // no foreign key: the join condition is guessed from column names
        items = At("SELECT * FROM Orders o JOIN Notes n ON |", out c);
        Check(items[0].Text == "n.Order_Id = o.Id" && items[0].Detail == "matching column names", "Notes.Order_Id is matched to Orders.Id");
        items = At("SELECT * FROM Notes n JOIN Orders o |", out c);
        Check(items[0].Text == "ON o.Id = n.Order_Id", "and the other way round");
        items = At("SELECT * FROM Orders o JOIN Shipments s |", out c);
        Check(items[0].Text == "ON s.CustomerId = o.CustomerId", "a shared ...Id column is matched");
        items = At("SELECT * FROM Customers c JOIN Orders o ON |", out c);
        Check(items[0].Text == "o.CustomerId = c.Id" && items[0].Detail == "foreign key" && items.Count(i => i.Kind == ItemKind.Join) == 1, "a foreign key wins over guesses");
        At("SELECT * FROM Orders AS |", out c);
        Check(c.Mode == Mode.None, "nothing after AS");
        At("SELECT 'a |", out c);
        Check(c.Mode == Mode.None, "nothing inside a string");
        At("SELECT 1 -- note |", out c);
        Check(c.Mode == Mode.None, "nothing inside a comment");
        At("SELECT @va|", out c);
        Check(c.Mode == Mode.None, "nothing for variables");

        items = At("EXEC |", out c);
        Check(items.Count == 1 && items[0].Text == "GetOrders", "EXEC lists procedures");
        items = At("USE |", out c);
        Check(find(items, "Shop") != null, "USE lists databases");
        items = At("SELECT 1;\nss|", out c);
        Check(c.Mode == Mode.StatementStart && find(items, "ssf")?.Insert == "SELECT * FROM " && find(items, "SELECT") != null, "statement start offers snippets and keywords");

        // "*" then Tab
        Func<string, string> star = sql =>
        {
            int caret = sql.IndexOf('|');
            sql = sql.Remove(caret, 1);
            var e = Completion.ExpandStar(sql, caret, Shop());
            return e == null ? null : sql.Remove(e.Start, e.Length).Insert(e.Start, e.Text);
        };
        Check(star("SELECT *| FROM Orders") == "SELECT Id, CustomerId, Total FROM Orders", "* expands to the table's columns");
        Check(star("SELECT TOP (5) *| FROM Orders o JOIN Customers c ON c.Id = o.CustomerId")
            == "SELECT TOP (5) o.Id, o.CustomerId, o.Total, c.Id, c.Name FROM Orders o JOIN Customers c ON c.Id = o.CustomerId", "* expands every table, alias-qualified");
        Check(star("SELECT o.Id, c.*| FROM Orders o JOIN Customers c ON c.Id = o.CustomerId")
            == "SELECT o.Id, c.Id, c.Name FROM Orders o JOIN Customers c ON c.Id = o.CustomerId", "alias.* expands that table only");
        Check(star("SELECT od.*| FROM Sales.OrderDetails od") == "SELECT od.OrderId, od.Qty, od.[Order Note] FROM Sales.OrderDetails od", "expanded names are bracketed when needed");
        Check(star("SELECT COUNT(*|) FROM Orders") == null, "COUNT(*) is left alone");
        Check(star("SELECT Total *| FROM Orders") == null, "a multiplication is left alone");
        Check(star("SELECT *| FROM Nowhere") == null, "unknown tables: nothing to expand");
        Check(star("SELECT * |FROM Orders") == null, "only straight after the star");

        // column picker
        Func<string, string> pick = sql =>
        {
            int caret = sql.IndexOf('|');
            var c = Completion.ColumnChoices(sql.Remove(caret, 1), caret, Shop());
            return c == null ? null : c.Start + "+" + c.Length + ":" + string.Join(",", c.Columns.Select(x => x.Text));
        };
        Check(pick("SELECT | FROM Orders") == "7+0:Id,CustomerId,Total", "picker lists the table's columns");
        Check(pick("SELECT Cu| FROM Orders o JOIN Customers c ON c.Id = o.CustomerId") == "7+2:o.Id,o.CustomerId,o.Total,c.Id,c.Name", "picker replaces the word being typed, across all tables");
        Check(pick("SELECT c.| FROM Orders o JOIN Customers c ON c.Id = o.CustomerId") == "7+2:c.Id,c.Name", "picker after alias. offers that table and replaces the alias");
        Check(pick("SELECT *| FROM Orders o") == "7+1:o.Id,o.CustomerId,o.Total", "picker after * replaces the star");
        Check(pick("SELECT | FROM Nowhere") == null && pick("SELECT 'x |' FROM Orders") == null, "no picker without known tables or inside a string");
        items = At("SELECT | FROM Orders", out c);
        Check(items[0].Text == Completion.PickColumnsText && items[0].Insert == "", "the column list starts with the picker entry");

        // picked columns are laid out like an expanded star, wherever they were put
        Func<string, string, string> picked = (sql, columns) =>
        {
            int caret = sql.IndexOf('|');
            sql = sql.Remove(caret, 1);
            var ch = Completion.ColumnChoices(sql, caret, Shop());
            string laidOut = Completion.WithColumnsLaidOut(sql, ch.Start, ch.Length, columns, out int end);
            Check(laidOut.Substring(0, end).EndsWith(columns.Substring(columns.LastIndexOf(' ') + 1)), "the caret ends up after the picked columns: " + sql);
            return laidOut;
        };
        Same("SELECT\n    Id,\n    Total\nFROM Orders", picked("select | from Orders", "Id, Total"), "picked columns go one per line");
        Same("SELECT\n    o.Id,\n    c.Name\nFROM Orders o\nINNER JOIN Customers c\n    ON c.Id = o.CustomerId", picked("select *| from Orders o inner join Customers c on c.Id = o.CustomerId", "o.Id, c.Name"), "picked columns replace a star, one per line");
        Same("select 1;\nSELECT Id\nFROM Orders\nORDER BY Id, Total", picked("select 1;\nselect Id from Orders order by |", "Id, Total"), "picking in another clause formats that statement only");

        // history
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SQLFinishCheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check(History.Load(folder).Count == 0, "history: no folder, no entries");
            var t0 = new DateTime(2026, 11, 13, 10, 0, 0);
            string odd = "-- a comment first\r\nSELECT 'tab\there'\r\n\r\nGO\r\n";
            History.Save(folder, new HistoryEntry { When = t0, Server = @"(localdb)\X", Database = "Shop", Sql = odd }, 3);
            for (int i = 1; i <= 4; i++)
                History.Save(folder, new HistoryEntry { When = t0.AddSeconds(i), Server = "s", Database = null, Sql = "SELECT " + i }, 3);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "stray.sql"), "not ours");
            var loaded = History.Load(folder);
            Check(string.Join(",", loaded.Select(h => h.Sql)) == "SELECT 4,SELECT 3,SELECT 2", "history: newest first, only the last 3 kept, stray files skipped");
            Check(loaded[0].When == t0.AddSeconds(4) && loaded[0].Server == "s" && loaded[0].Database == "", "history: time, server and database round-trip");
            History.Save(folder, new HistoryEntry { When = t0.AddSeconds(9), Server = @"(localdb)\X", Database = "Shop", Sql = odd }, 3);
            var newest = History.Load(folder)[0];
            Check(newest.Sql == odd && newest.Server == @"(localdb)\X" && newest.Database == "Shop", "history: the script is kept exactly");

            // pinned scripts outlive pruning; deleting forgets one script
            History.SetPinned(newest, true);
            for (int i = 20; i <= 24; i++)
                History.Save(folder, new HistoryEntry { When = t0.AddSeconds(i), Server = "s", Sql = "SELECT " + i }, 3);
            loaded = History.Load(folder);
            Check(string.Join(",", loaded.Select(h => (h.Pinned ? "*" : "") + h.Sql.Trim().Split('\n').Last())) == "SELECT 24,SELECT 23,SELECT 22,*GO", "history: a pinned script is kept beyond the limit, and not counted in it");
            var pinned = loaded.Single(h => h.Pinned);
            Check(pinned.Sql == odd && pinned.When == t0.AddSeconds(9), "history: pinning changes nothing but the pin");
            History.SetPinned(pinned, false);
            History.Delete(loaded[0]);
            Check(string.Join(",", History.Load(folder).Select(h => (h.Pinned ? "*" : "") + h.Sql.Trim().Split('\n').Last())) == "SELECT 23,SELECT 22,GO", "history: unpinned and deleted");
            History.Save(folder, new HistoryEntry { When = t0.AddSeconds(30), Server = "s", Sql = "SELECT 30" }, 3);
            Check(History.Load(folder).Count == 3 && History.Load(folder).All(h => h.Sql != odd), "history: an unpinned script is pruned like any other");
        }
        finally
        {
            if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true);
        }

        // F12: the object named at the caret, and the script that modifies it
        Func<string, string> nameAt = sql =>
        {
            int caret = sql.IndexOf('|');
            var parts = Definition.NameAt(sql.Remove(caret, 1), caret);
            return parts == null ? null : string.Join("/", parts);
        };
        Check(nameAt("EXEC dbo.Get|Orders @a = 1") == "dbo/GetOrders", "name at the caret, with its schema");
        Check(nameAt("EXEC d|bo.GetOrders") == "dbo/GetOrders" && nameAt("EXEC dbo.GetOrders|") == "dbo/GetOrders", "caret in the schema, or at the end of the name");
        Check(nameAt("EXEC |GetOrders") == "GetOrders" && nameAt("EXEC Shop..[Get |Orders]") == "Shop//Get Orders", "plain, and database..name with a bracketed name");
        Check(nameAt("EXEC Shop . dbo . GetOrders|") == "Shop/dbo/GetOrders", "three parts, spaced out");
        Check(nameAt("EXEC| dbo.GetOrders") == null && nameAt("SELECT @a|") == null && nameAt("EXEC srv.Shop.dbo.Get|Orders") == null, "keywords, variables and linked servers are not names to open");
        var module = new Module { Database = "Sh]op", Schema = "dbo", Name = "GetOrders", AnsiNulls = true, QuotedIdentifier = false, Text = "-- create notes\r\ncreate   proc dbo.GetOrders as\r\nselect 'create'\r\n\r\n" };
        Same("USE [Sh]]op]\nGO\nSET ANSI_NULLS ON\nGO\nSET QUOTED_IDENTIFIER OFF\nGO\n-- create notes\nALTER   proc dbo.GetOrders as\nselect 'create'\nGO\n", Definition.ModifyScript(module), "the modify script alters, under the module's own settings");
        module.Text = "CREATE OR ALTER VIEW v AS SELECT 1 AS a";
        Check(Definition.ModifyScript(module).Contains("\r\nCREATE OR ALTER VIEW v AS SELECT 1 AS a\r\nGO"), "CREATE OR ALTER is left as it is");

        // UPDATE / DELETE without WHERE
        Func<string, string> risky = sql => string.Join("; ", Guard.Unfiltered(sql));
        Check(risky("UPDATE dbo.Orders SET Total = 0") == "UPDATE dbo.Orders (line 1)", "UPDATE without WHERE is flagged");
        Check(risky("SELECT 1\nDELETE FROM Orders\nSELECT 2 WHERE 1 = 1") == "DELETE Orders (line 2)", "a later statement's WHERE does not count");
        Check(risky("UPDATE o SET Total = CASE WHEN Total > 5 THEN 1 ELSE 2 END FROM Orders o WHERE o.Id = 1") == "", "WHERE after a CASE is seen");
        Check(risky("UPDATE Orders SET Total = (SELECT MAX(Total) FROM Orders WHERE Id = 1)") == "UPDATE Orders (line 1)", "a subquery's WHERE does not count");
        Check(risky("DELETE TOP (10) FROM Orders WHERE Id > 5; UPDATE Orders SET Total = 1 WHERE Id = 2") == "", "filtered statements pass");
        Check(risky("DELETE FROM #work; UPDATE @t SET a = 1; -- delete from Orders\nSELECT 'delete from Orders'") == "", "temp tables, comments and strings are ignored");
        Check(risky("CREATE TRIGGER tr ON Orders AFTER INSERT, UPDATE, DELETE AS SELECT 1; SELECT * FROM t FOR UPDATE; GRANT UPDATE ON Orders TO x") == "", "UPDATE / DELETE inside other statements is ignored");
        Check(risky("MERGE Orders t USING s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET t.Total = s.Total WHEN NOT MATCHED BY SOURCE THEN DELETE;") == "", "MERGE actions are ignored");
        Check(risky("BEGIN TRAN\nDELETE Orders\nROLLBACK") == "DELETE Orders (line 2)", "DELETE without FROM is flagged");
        Check(Guard.ChangesSchema("alter table t add c int") && !Guard.ChangesSchema("select 'create' from t -- drop"), "schema changes are recognised");

        Check(Completion.AliasFor("OrderDetails", new string[0]) == "od", "alias from initials");
        Check(Completion.AliasFor("Orders", new[] { "o" }) == "o2", "alias avoids ones in use");
        Check(Completion.AliasFor("OrderNotes", new string[0]) == "on2", "alias avoids keywords");
        Check(Completion.DatabaseAt("USE Shop\nGO\nSELECT 1", 20) == "Shop", "USE sets the database");
    }

    /// <summary>Writes a small workbook and reads its parts back.</summary>
    static void XlsxChecks()
    {
        var columns = new[] { "Id:int", "Big:bigint", "Amount:decimal:2", "Price:money:4", "Ratio:float", "Day:date", "Changed:datetime:3", "At:time:7", "Active:bit", "Note <&>:nvarchar", "Any:sql_variant" }
            .Select(c => c.Split(':')).Select(p => new XlsxColumn { Name = p[0], SqlType = p[1], Scale = p.Length > 2 ? int.Parse(p[2]) : 0 }).ToList();
        var noon = new DateTime(2026, 11, 13, 12, 0, 0); // day 46339 in Excel
        object[][] data =
        {
            new object[] { new SqlInt32(1), new SqlInt64(42), new SqlDecimal(12.50m), new SqlMoney(3.5m), new SqlDouble(0.1), noon.Date, new SqlDateTime(noon), new TimeSpan(6, 0, 0).Add(TimeSpan.FromTicks(9999)), SqlBoolean.True, "a<b & _x0041_ c\r\nd" + (char)1, noon.AddTicks(9999) },
            new object[] { SqlInt32.Null, new SqlInt64(1234567890123456), new SqlDecimal(1234567890.123456789m), null, DBNull.Value, new DateTime(1850, 1, 2), null, null, SqlBoolean.False, new string('x', Xlsx.MaxCellChars + 5), "text" },
        };
        Func<long, int, string> text = (r, c) => data[r][c] is DateTime d ? d.ToString("yyyy-MM-dd") : Convert.ToString(data[r][c], CultureInfo.InvariantCulture);

        var parts = new Dictionary<string, string>();
        using (var stream = new MemoryStream())
        {
            long rows = Xlsx.Write(stream, columns, data.Length, (r, c) => data[r][c], text, out int truncated);
            Check(rows == 2 && truncated == 1, "xlsx: both rows are written and the over-long cell is counted");
            stream.Position = 0;
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                foreach (var entry in zip.Entries)
                    using (var reader = new StreamReader(entry.Open()))
                    {
                        parts[entry.FullName] = reader.ReadToEnd();
                        try { XDocument.Parse(parts[entry.FullName]); }
                        catch (System.Xml.XmlException e) { Check(false, "xlsx: " + entry.FullName + " is not well-formed XML: " + e.Message); }
                    }
        }
        Check(string.Join(",", parts.Keys.OrderBy(k => k, StringComparer.Ordinal)) == "[Content_Types].xml,_rels/.rels,xl/_rels/workbook.xml.rels,xl/styles.xml,xl/workbook.xml,xl/worksheets/sheet1.xml", "xlsx: the workbook has its six parts");
        string sheet = parts["xl/worksheets/sheet1.xml"], styles = parts["xl/styles.xml"];
        Func<string, bool> has = xml => sheet.Contains(xml);
        Func<string, string> cellText = reference => "<c r=\"" + reference + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">";

        Check(has("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>"), "xlsx: the header row is frozen");
        Check(has("<autoFilter ref=\"A1:K3\"/>"), "xlsx: the filter covers the header and every row");
        Check(has("<c r=\"A1\" s=\"1\" t=\"inlineStr\"><is><t xml:space=\"preserve\">Id</t>") && has(">Note &lt;&amp;&gt;</t>"), "xlsx: headings use the header style and are escaped");
        Check(styles.Contains("<fgColor rgb=\"FF1F4E78\"/>") && styles.Contains("<b/>"), "xlsx: the header style is bold on a coloured fill");
        Check(styles.Contains("numFmtId=\"164\" formatCode=\"0\"") && styles.Contains("numFmtId=\"165\" formatCode=\"0.00\"") && styles.Contains("formatCode=\"yyyy-mm-dd\""), "xlsx: one number format per column type");
        Check(has("<col min=\"3\" max=\"3\" width=\"22\" style=\"3\" customWidth=\"1\"/>"), "xlsx: columns carry their width and format");

        Check(has("<c r=\"A2\" s=\"2\"><v>1</v></c>") && has("<c r=\"B2\" s=\"2\"><v>42</v></c>"), "xlsx: integers are numbers");
        Check(has("<c r=\"C2\" s=\"3\"><v>12.50</v></c>") && has("<c r=\"D2\" s=\"4\"><v>3.5000</v></c>") && has("<c r=\"E2\"><v>0.1</v></c>"), "xlsx: decimal, money and float are numbers");
        Check(has("<c r=\"F2\" s=\"5\"><v>46339</v></c>") && has("<c r=\"G2\" s=\"6\"><v>46339.5</v></c>") && has("<c r=\"H2\" s=\"7\"><v>0.25</v></c>"), "xlsx: date, datetime and time are Excel dates, cut to the millisecond");
        Check(has("<c r=\"I2\" t=\"b\"><v>1</v></c>") && has("<c r=\"I3\" t=\"b\"><v>0</v></c>"), "xlsx: bit is a boolean");
        Check(has(cellText("J2") + "a&lt;b &amp; _x005F_x0041_ c\nd_x0001_</t>"), "xlsx: text is escaped, CRLF becomes a line break, control characters use Excel's escape");
        Check(has("<c r=\"K2\" s=\"6\"><v>46339.5</v></c>") && has(cellText("K3") + "text</t>"), "xlsx: a date in an untyped column still gets a date format");

        Check(!has("r=\"A3\"") && !has("r=\"D3\"") && !has("r=\"E3\"") && !has("r=\"G3\""), "xlsx: NULL is an empty cell");
        Check(has(cellText("B3") + "1234567890123456</t>") && has(cellText("C3") + "1234567890.123456789</t>"), "xlsx: numbers beyond Excel's 15 digits are kept exactly, as text");
        Check(has(cellText("F3") + "1850-01-02</t>"), "xlsx: a date before 1900 is text");
        Check(has(cellText("J3") + new string('x', Xlsx.MaxCellChars) + "</t>"), "xlsx: text is cut at Excel's cell limit");

        // a workbook that is only there to be opened in Excel
        string scratch = Path.Combine(Path.GetTempPath(), "SQLFinishCheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = new DateTime(2026, 11, 23, 11, 37, 5);
            Directory.CreateDirectory(scratch);
            string stale = Path.Combine(scratch, "old.xlsx"), recent = Path.Combine(scratch, "recent.xlsx");
            File.WriteAllText(stale, "x");
            File.WriteAllText(recent, "x");
            File.SetLastWriteTime(stale, now.AddDays(-8));
            File.SetLastWriteTime(recent, now.AddDays(-1));
            File.SetAttributes(stale, FileAttributes.ReadOnly);
            Check(Xlsx.ScratchFile(scratch, "a<b>:c", now) == Path.Combine(scratch, "a_b__c 2026-11-23 113705.xlsx"), "xlsx: the temporary workbook is named for the window and the time");
            Check(!File.Exists(stale) && File.Exists(recent), "xlsx: temporary workbooks a week old are cleared out, read-only or not");
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }

        Check(Xlsx.Format("DECIMAL", 0) == "0"&& Xlsx.Format("numeric", 4) == "0.0000" && Xlsx.Format("datetime2", 0) == "yyyy-mm-dd hh:mm:ss" && Xlsx.Format("time", 0) == "hh:mm:ss", "xlsx: formats follow the scale");
        Check(Xlsx.Format("nvarchar", 0) == null && Xlsx.Format("uniqueidentifier", 0) == null && Xlsx.Format(null, 0) == null, "xlsx: other types are left as General");
    }

    /// <summary>The open query windows are noted, forgotten only when closed on purpose, and found again by the next start.</summary>
    static void SessionChecks()
    {
        string root = Path.Combine(Path.GetTempPath(), "SQLFinishCheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            var started = new DateTime(2026, 11, 23, 9, 0, 0);
            var t = started.AddHours(1);
            string mine = Path.Combine(root, "111-" + started.Ticks);
            Func<string, string, SessionWindow, KeyValuePair<string, SessionWindow>> window = (key, text, w) => { w.Text = text; return new KeyValuePair<string, SessionWindow>(key, w); };
            Func<int, DateTime?> running = id => id == 111 ? started : (DateTime?)null, nothingRunning = id => null;
            Check(Session.Abandoned(root, nothingRunning).Count == 0, "session: nothing to reopen before anything was noted");

            var session = new Session(root, 111, started);
            string odd = "select 'tab\there'\r\n\r\n-- caf\u00e9\r\n";
            session.Save(new[] { window("a", odd, new SessionWindow { Connection = "<c>1</c>", Active = true }), window("b", "B", new SessionWindow { Path = @"C:\q\b.sql" }) }, t);
            Check(File.ReadAllText(Path.Combine(mine, "0000.sql")) == odd && File.ReadAllText(Path.Combine(mine, "0001.sql")) == "B", "session: each window's text is kept exactly");
            session.Save(new[] { window("a", null, new SessionWindow { Connection = "<c>1</c>", Active = true }) }, t.AddSeconds(10));
            Check(File.ReadAllText(Path.Combine(mine, "0000.sql")) == odd && File.Exists(Path.Combine(mine, "0001.sql")), "session: unchanged text is left alone, and a window just closed is not forgotten yet");
            Check(Session.Abandoned(root, running).Count == 0, "session: a running SSMS's windows are not up for reopening");

            // SSMS ends here, seconds after window b went: both come back
            var abandoned = Session.Abandoned(root, nothingRunning);
            Check(abandoned.Count == 1 && abandoned[0] == mine, "session: found once its SSMS is gone");
            Check(Session.Abandoned(root, id => started.AddSeconds(5)).Count == 1, "session: a new process with the old one's id is not mistaken for it");
            var loaded = Session.Load(mine);
            Check(loaded.Count == 2 && loaded[0].Active && loaded[0].Connection == "<c>1</c>" && loaded[0].Path == "" && File.ReadAllText(loaded[0].TextFile) == odd
                && !loaded[1].Active && loaded[1].Path == @"C:\q\b.sql" && loaded[1].Connection == "", "session: windows load in order with their file, connection and text");

            // had SSMS lived on instead, the closed window would be forgotten, and a changed one rewritten
            session.Save(new[] { window("a", "A2", new SessionWindow()) }, t.AddSeconds(45));
            loaded = Session.Load(mine);
            Check(loaded.Count == 1 && File.ReadAllText(loaded[0].TextFile) == "A2" && !loaded[0].Active && loaded[0].Connection == "", "session: a window that stays closed is forgotten; changes are noted");
            session.Save(new[] { window("c", "C", new SessionWindow()) }, t.AddSeconds(50));
            Check(Session.Load(mine).Count == 2 && File.Exists(Path.Combine(mine, "0002.sql")), "session: a new window never takes over a closed one's files");

            string claimed = Session.Claim(mine);
            Check(Session.Load(claimed).Count == 2 && !Directory.Exists(mine), "session: claimed for reopening");
            bool second = false;
            try { Session.Claim(mine); second = true; } catch (IOException) { }
            Check(!second, "session: a session is claimed only once");
            Check(Session.Abandoned(root, nothingRunning).Count == 0 && Directory.Exists(mine + ".failed") && !Directory.Exists(claimed), "session: one that never finished reopening is set aside, not retried");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>Runs the real catalog query against LocalDB, if there is one.</summary>
    static void LiveCatalogCheck()
    {
        const string db = "SQLFinishCheck";
        string server = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Connect Timeout=30;TrustServerCertificate=true;";
        try
        {
            using (var cn = new SqlConnection(server))
            {
                cn.Open();
                Action<string> run = sql => { using (var cmd = new SqlCommand(sql, cn)) cmd.ExecuteNonQuery(); };
                run($"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE {db} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {db} END; CREATE DATABASE {db}");
                try
                {
                    cn.ChangeDatabase(db);
                    run("CREATE TABLE dbo.Customers (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL)");
                    run("CREATE TABLE dbo.Orders (Id int NOT NULL PRIMARY KEY, CustomerId int NOT NULL CONSTRAINT FK_O_C REFERENCES dbo.Customers (Id), Total decimal(10,2) NULL)");
                    run("CREATE PROCEDURE dbo.GetOrders AS SELECT * FROM dbo.Orders");
                    var s = Schema.Load(cn);
                    var orders = s.Find(null, "orders");
                    Check(orders != null && orders.Kind == 'U' && orders.Columns.Count == 3, "live: tables and columns load");
                    Check(orders?.Columns.FirstOrDefault(c => c.Name == "Total")?.Type == "decimal(10,2) NULL", "live: column types load");
                    Check(s.Find("dbo", "GetOrders")?.Kind == 'P', "live: procedures load");
                    Check(s.ForeignKeys.Count == 1 && s.ForeignKeys[0].ToTable == "Customers" && s.ForeignKeys[0].FromColumn == "CustomerId", "live: foreign keys load");
                    Check(s.Databases.Contains(db), "live: databases load");
                    var proc = Definition.Load(cn, new List<string> { "getorders" });
                    Check(proc != null && proc.Database == db && proc.Schema == "dbo" && proc.Name == "GetOrders" && proc.AnsiNulls && proc.QuotedIdentifier
                        && proc.Text == "CREATE PROCEDURE dbo.GetOrders AS SELECT * FROM dbo.Orders", "live: a procedure's source loads");
                    Check(Definition.Load(cn, new List<string> { db, "", "GetOrders" })?.Name == "GetOrders", "live: database..name resolves");
                    Check(Definition.Load(cn, new List<string> { "dbo", "Orders" }) is Module table && table.Text == null, "live: a table is found but has no source");
                    Check(Definition.Load(cn, new List<string> { "dbo", "Nowhere" }) == null, "live: an unknown name is not found");
                }
                finally
                {
                    cn.ChangeDatabase("master");
                    run($"ALTER DATABASE {db} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {db}");
                }
            }
            Console.WriteLine("live catalog check ran against LocalDB");
        }
        catch (SqlException e)
        {
            Console.WriteLine("live catalog check SKIPPED (no LocalDB): " + e.Message.Split('\n')[0]);
        }
    }
}
