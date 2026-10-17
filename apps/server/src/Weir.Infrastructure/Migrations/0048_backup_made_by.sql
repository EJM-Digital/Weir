-- Weir schema 0048 (revision 0083_backup_made_by): who made a backup.
--
-- System > Backups shows the last backup, and a backup asked for by hand has to say it was by hand, apart from the ones the
-- schedule makes. 'schedule' or 'manual'. A backup made before this column existed keeps NULL: nothing recorded who made it.
ALTER TABLE suite_configuration_backup ADD COLUMN made_by TEXT;
