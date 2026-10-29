-- Weir schema 0052 (revision 0087_recovery_codes): recovery codes.
--
-- The way back in when the password is forgotten. A set is ten single-use codes for the one account, shown once when they are made.
-- Only a hash of each code is kept (Argon2id, at a smaller cost than a password: a code is 100 random bits); `lookup` is the code's first group, in the clear, so a typed
-- code is checked against one hash and not ten. `confirmed_at` is set when the person says they saved the set, `used_at` when a
-- code is spent. Making a new set deletes the old rows. An account that exists already has no codes until it makes a set.
CREATE TABLE recovery_codes (
	id INTEGER PRIMARY KEY AUTOINCREMENT,
	user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
	lookup TEXT NOT NULL UNIQUE,
	code_hash TEXT NOT NULL,
	created_at DATETIME DEFAULT CURRENT_TIMESTAMP NOT NULL,
	confirmed_at DATETIME,
	used_at DATETIME
);

CREATE INDEX ix_recovery_codes_user_id ON recovery_codes (user_id);
