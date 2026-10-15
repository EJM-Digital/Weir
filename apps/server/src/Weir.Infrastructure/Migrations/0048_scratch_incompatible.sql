-- Scratch, never merged: a migration that renames every account, so the same password no longer finds it (#986).
UPDATE users SET username = username || '-migrated';
