// Engine self-check: dotnet run --project check
// Exits non-zero if the formatter, completion or the live catalog query misbehave.
using System;
using System.Collections.Generic;
using System.Linq;
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
