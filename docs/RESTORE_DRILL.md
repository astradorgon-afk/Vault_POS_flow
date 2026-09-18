# VaultFlow restore drill

The database backup is a PostgreSQL custom-format dump. Backups contain
business data and must be stored in encrypted, access-controlled storage with
retention appropriate to the organization.

## Backup

From the deployment host, while the compose stack is running:

```powershell
./scripts/backup-postgres.ps1 -OutputDirectory D:/vaultflow-backups
```

The script refuses an empty dump and leaves the original database untouched.
Record the filename, byte size and storage location in the operations log.

## Restore drill

Restore only into a maintenance window or an isolated recovery stack:

```powershell
./scripts/restore-postgres.ps1 -BackupFile D:/vaultflow-backups/vaultflow-YYYYMMDD-HHMMSS.dump
```

The command requires typing `RESTORE` because `pg_restore --clean` replaces
objects in the target database. Afterward:

1. Run the migration/model check against the recovered database.
2. Check `/health/ready` and sign in with a known recovery account.
3. Verify one sale, one inventory balance, one audit record and one notification.
4. Verify the API role still cannot perform DDL or rewrite append-only ledger rows.
5. Record elapsed restore time, dump size and any missing operational artifacts.

The restore drill is successful only when the recovered stack passes those
checks and the measured recovery time is within the organization's target.
