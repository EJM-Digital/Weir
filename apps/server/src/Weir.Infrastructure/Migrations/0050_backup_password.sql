-- Weir schema 0050 (revision 0085_backup_password): the backup password.
--
-- A backup seals the connections' secrets with a key made from a password the person sets. Weir keeps that key, locked with the
-- install's credentials secret like every other stored secret, so the schedule can seal a backup while nobody is signed in. The
-- password itself is never stored. One row at most. A backup records whether it holds sealed secrets; one made before this
-- column existed does not.
CREATE TABLE backup_password (
	id INTEGER PRIMARY KEY CHECK (id = 1),
	salt TEXT NOT NULL,
	key_ciphertext TEXT NOT NULL,
	updated_at DATETIME DEFAULT CURRENT_TIMESTAMP NOT NULL
);

ALTER TABLE suite_configuration_backup ADD COLUMN secrets_sealed BOOLEAN DEFAULT '0' NOT NULL;
