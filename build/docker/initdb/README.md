# Database initialisation

`01-roles.sql` runs once when the PostgreSQL volume is first created. It creates
the two non-owning roles and takes `CREATE` away from `PUBLIC`.

`02-grants.sql` is **not** an init script: it must run *after* the migrator has
created the tables, because it narrows `pos_app` to `SELECT, INSERT` on the
append-only tables. It is applied as a deployment step, and re-applying it is
safe.

Passwords come from the environment (`POS_APP_PASSWORD`, `POS_READONLY_PASSWORD`),
which compose populates from Docker secrets. No password is ever written here.
