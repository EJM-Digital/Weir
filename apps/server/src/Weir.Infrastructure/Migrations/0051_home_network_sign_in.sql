-- Weir schema 0051 (revision 0086_home_network_sign_in): skip sign-in for devices on the home network.
--
-- One row at most. Without a row the setting is off, which is what every install did before it existed. The owner's own
-- networks and the allow list that narrows who skips are lists of addresses or CIDR ranges, one per line.
CREATE TABLE home_network_sign_in (
	id INTEGER PRIMARY KEY CHECK (id = 1),
	enabled BOOLEAN DEFAULT '0' NOT NULL,
	extra_networks TEXT DEFAULT '' NOT NULL,
	allow_list TEXT DEFAULT '' NOT NULL,
	updated_at DATETIME DEFAULT CURRENT_TIMESTAMP NOT NULL
);
