-- Weir schema 0047 (revision 0082_connection_links_are_kept): a media manager connection cannot be deleted while a workflow is
-- linked to it.
--
-- A link between a workflow and a connection was deleted with the connection (ON DELETE CASCADE), so removing a connection quietly
-- turned every workflow it fed into one Weir feeds itself: its watched folder is then scanned and the originals a download client is
-- still seeding can be removed. The connection's link now restricts its deletion, so the database refuses what the API refuses
-- (Weir unlinks or removes the workflows first). A link is still deleted with its workflow.
CREATE TABLE library_manager_links__new (
	id INTEGER NOT NULL,
	library_id INTEGER NOT NULL,
	connection_id INTEGER NOT NULL,
	CONSTRAINT pk_library_manager_links PRIMARY KEY (id),
	CONSTRAINT uq_library_manager_links_pair UNIQUE (library_id, connection_id),
	CONSTRAINT fk_library_manager_links_libraries_library_id FOREIGN KEY(library_id) REFERENCES libraries (id) ON DELETE CASCADE,
	CONSTRAINT fk_library_manager_links_media_manager_connections_connection_id FOREIGN KEY(connection_id) REFERENCES media_manager_connections (id) ON DELETE RESTRICT
);

INSERT INTO library_manager_links__new (id, library_id, connection_id) SELECT id, library_id, connection_id FROM library_manager_links;

DROP TABLE library_manager_links;

ALTER TABLE library_manager_links__new RENAME TO library_manager_links;
