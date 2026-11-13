using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace SQLFinish.Engine
{
    public sealed class Column
    {
        public string Name, Type;
    }

    public sealed class DbObject
    {
        public string Schema, Name;
        public char Kind; // U table, V view, P procedure, F scalar function, T table function
        public List<Column> Columns = new List<Column>();
        public bool IsTableLike => Kind == 'U' || Kind == 'V';
    }

    public sealed class ForeignKey
    {
        public string Name, FromSchema, FromTable, FromColumn, ToSchema, ToTable, ToColumn;
    }

    /// <summary>What completion knows about one database.</summary>
    public sealed class Schema
    {
        public static readonly Schema Empty = new Schema();

        public List<DbObject> Objects = new List<DbObject>();
        public List<ForeignKey> ForeignKeys = new List<ForeignKey>();
        public List<string> Databases = new List<string>();

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Finds an object by name; an omitted schema prefers dbo.</summary>
        public DbObject Find(string schema, string name)
        {
            var hits = Objects.Where(o => Eq(o.Name, name) && (schema == null || Eq(o.Schema, schema))).ToList();
            return hits.FirstOrDefault(o => Eq(o.Schema, "dbo")) ?? hits.FirstOrDefault();
        }

        public bool HasSchema(string name) => Objects.Any(o => Eq(o.Schema, name));

        const string Query = @"
SELECT s.name, o.name, RTRIM(o.type)
FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type IN ('U','V','P','FN','IF','TF') AND o.is_ms_shipped = 0
ORDER BY s.name, o.name;

SELECT s.name, o.name, c.name,
       TYPE_NAME(c.user_type_id)
       + CASE WHEN TYPE_NAME(c.system_type_id) IN ('varchar','char','varbinary','binary')
                  THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
              WHEN TYPE_NAME(c.system_type_id) IN ('nvarchar','nchar')
                  THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
              WHEN TYPE_NAME(c.system_type_id) IN ('decimal','numeric')
                  THEN '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
              ELSE '' END
       + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END
FROM sys.columns c
JOIN sys.objects o ON o.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type IN ('U','V','IF','TF') AND o.is_ms_shipped = 0
ORDER BY c.object_id, c.column_id;

SELECT fk.name, ps.name, pt.name, pc.name, rs.name, rt.name, rc.name
FROM sys.foreign_key_columns k
JOIN sys.foreign_keys fk ON fk.object_id = k.constraint_object_id
JOIN sys.tables pt ON pt.object_id = k.parent_object_id
JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
JOIN sys.columns pc ON pc.object_id = k.parent_object_id AND pc.column_id = k.parent_column_id
JOIN sys.tables rt ON rt.object_id = k.referenced_object_id
JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
JOIN sys.columns rc ON rc.object_id = k.referenced_object_id AND rc.column_id = k.referenced_column_id
ORDER BY fk.object_id, k.constraint_column_id;

SELECT name FROM sys.databases ORDER BY name;";

        /// <summary>Reads the catalog of the connection's current database. The connection must be open.</summary>
        public static Schema Load(IDbConnection connection)
        {
            var schema = new Schema();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = Query;
                cmd.CommandTimeout = 30;
                using (var r = cmd.ExecuteReader())
                {
                    var byName = new Dictionary<string, DbObject>(StringComparer.OrdinalIgnoreCase);
                    while (r.Read())
                    {
                        string type = r.GetString(2);
                        var o = new DbObject
                        {
                            Schema = r.GetString(0),
                            Name = r.GetString(1),
                            Kind = type == "FN" ? 'F' : type == "IF" || type == "TF" ? 'T' : type[0],
                        };
                        schema.Objects.Add(o);
                        byName[o.Schema + "\n" + o.Name] = o;
                    }
                    r.NextResult();
                    while (r.Read())
                        if (byName.TryGetValue(r.GetString(0) + "\n" + r.GetString(1), out var o))
                            o.Columns.Add(new Column { Name = r.GetString(2), Type = r.IsDBNull(3) ? "" : r.GetString(3) });
                    r.NextResult();
                    while (r.Read())
                        schema.ForeignKeys.Add(new ForeignKey
                        {
                            Name = r.GetString(0),
                            FromSchema = r.GetString(1), FromTable = r.GetString(2), FromColumn = r.GetString(3),
                            ToSchema = r.GetString(4), ToTable = r.GetString(5), ToColumn = r.GetString(6),
                        });
                    r.NextResult();
                    while (r.Read()) schema.Databases.Add(r.GetString(0));
                }
            }
            return schema;
        }
    }
}
