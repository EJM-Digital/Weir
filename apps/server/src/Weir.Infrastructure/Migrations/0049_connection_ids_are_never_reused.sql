-- Weir schema 0049 (revision 0084_connection_ids_are_never_reused): a media manager connection's number is never given to another.
--
-- The next connection took the number of the highest one removed. Everything that is told about a connection by number (the
-- answer of a call still on its way, a test result being saved, the figures on its row, the line on the Activity stream) then
-- landed on the connection that took its place. A number now only goes up. Every column, and every number, is kept.
CREATE TABLE media_manager_connections__new (
	id INTEGER PRIMARY KEY AUTOINCREMENT,
	kind TEXT NOT NULL,
	name TEXT NOT NULL,
	enabled BOOLEAN DEFAULT '1' NOT NULL,
	base_url TEXT DEFAULT '' NOT NULL,
	api_key_ciphertext TEXT,
	webhook_secret_ciphertext TEXT,
	last_connection_test_ok BOOLEAN,
	last_connection_test_at DATETIME,
	last_connection_test_detail TEXT,
	created_at DATETIME DEFAULT CURRENT_TIMESTAMP NOT NULL,
	updated_at DATETIME DEFAULT CURRENT_TIMESTAMP NOT NULL,
	downloaded_scan_enabled BOOLEAN DEFAULT '0' NOT NULL,
	nickname TEXT,
	last_answer_ms INTEGER,
	last_used_at DATETIME,
	CONSTRAINT uq_media_manager_connections_name UNIQUE (name)
);

INSERT INTO media_manager_connections__new (
	id, kind, name, enabled, base_url, api_key_ciphertext, webhook_secret_ciphertext, last_connection_test_ok, last_connection_test_at,
	last_connection_test_detail, created_at, updated_at, downloaded_scan_enabled, nickname, last_answer_ms, last_used_at)
SELECT
	id, kind, name, enabled, base_url, api_key_ciphertext, webhook_secret_ciphertext, last_connection_test_ok, last_connection_test_at,
	last_connection_test_detail, created_at, updated_at, downloaded_scan_enabled, nickname, last_answer_ms, last_used_at
FROM media_manager_connections;

DROP TABLE media_manager_connections;

ALTER TABLE media_manager_connections__new RENAME TO media_manager_connections;
