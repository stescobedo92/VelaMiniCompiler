# vela.std.db.sqlite

Source-library stub documenting the Vela SQLite database API.

## Runtime wiring

Vela apps should declare the **`vela.core.sqlite`** capability (or `include vela.core.sqlite` in generated projects). The compiler emitter links **`Vela.Sqlite.Runtime`**, which provides:

| Vela surface | C# runtime |
|--------------|------------|
| `sqlite_open(path)` | `VelaSqlite.Open(path)` |
| `sqlite_execute(db, sql)` | `VelaSqlite.Execute(db, sql)` |
| `sqlite_query(db, sql)` | `VelaSqlite.Query(db, sql)` |
| `sqlite_migrate(db, dir)` | `VelaSqlite.Migrate(db, dir)` |
| `sqlite_close(db)` | `VelaSqlite.Close(db)` |

This package (`vela.std.db.sqlite`) is a **documentation stub** only. It does not ship the native adapter; capability selection pulls in `src/Vela.Sqlite.Runtime` at build time.

## Migrations

Place `*.sql` files in a directory. Files are applied in **sorted file-name order**. Applied migrations are tracked in the `_vela_migrations` table.
