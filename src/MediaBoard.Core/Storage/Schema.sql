-- Schema v4 for a new database. Applied once by LibraryDatabase when PRAGMA user_version is 0; an older
-- database is migrated step by step there instead.
-- Timestamps are ISO 8601 text; booleans are 0/1 integers.
-- Tables an overrides row can point at use AUTOINCREMENT so a deleted id is never reused (overrides has no foreign key).

CREATE TABLE folders (
    id       INTEGER PRIMARY KEY AUTOINCREMENT,
    path     TEXT    NOT NULL UNIQUE,
    enabled  INTEGER NOT NULL DEFAULT 1,
    added_at TEXT    NOT NULL
);

CREATE TABLE files (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    folder_id   INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
    rel_path    TEXT    NOT NULL,
    size        INTEGER NOT NULL,
    mtime       INTEGER NOT NULL,
    duration_s  REAL,
    first_seen  TEXT    NOT NULL,
    missing     INTEGER NOT NULL DEFAULT 0,
    parsed_json TEXT    NOT NULL,
    status      TEXT    NOT NULL DEFAULT 'pending'
                CHECK (status IN ('pending', 'matched', 'review', 'offline', 'not_media')),
    UNIQUE (folder_id, rel_path)
);
CREATE INDEX ix_files_status ON files(status);

CREATE TABLE titles (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    tmdb_id     INTEGER,
    kind        TEXT    NOT NULL,
    name        TEXT    NOT NULL,
    year        INTEGER,
    end_year    INTEGER,
    overview    TEXT,
    rating      REAL,
    poster_path TEXT,
    fetched_at  TEXT
);
CREATE UNIQUE INDEX ux_titles_tmdb ON titles(kind, tmdb_id) WHERE tmdb_id IS NOT NULL;

CREATE TABLE episodes (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    title_id    INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
    season      INTEGER NOT NULL,
    number      INTEGER NOT NULL,
    name        TEXT,
    runtime_min INTEGER,
    air_date    TEXT,
    UNIQUE (title_id, season, number)
);

CREATE TABLE file_titles (
    file_id    INTEGER NOT NULL REFERENCES files(id)  ON DELETE CASCADE,
    title_id   INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
    confidence REAL    NOT NULL,
    source     TEXT    NOT NULL CHECK (source IN ('auto', 'id_tag', 'user', 'rule')),
    PRIMARY KEY (file_id, title_id)
);
CREATE INDEX ix_file_titles_title ON file_titles(title_id);

CREATE TABLE file_episodes (
    file_id    INTEGER NOT NULL REFERENCES files(id)    ON DELETE CASCADE,
    episode_id INTEGER NOT NULL REFERENCES episodes(id) ON DELETE CASCADE,
    PRIMARY KEY (file_id, episode_id)
);
CREATE INDEX ix_file_episodes_episode ON file_episodes(episode_id);

CREATE TABLE match_rules (
    folder_id    INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
    show_folder  TEXT    NOT NULL DEFAULT '',
    parsed_title TEXT    NOT NULL,
    title_id     INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
    PRIMARY KEY (folder_id, show_folder, parsed_title)
);
CREATE INDEX ix_match_rules_title ON match_rules(title_id);

-- The accounts that can sign in. Row 1 is the owner, the person at this PC: no sign-in locally, and a password only
-- if they want to sign in from another PC. all_folders = 0 limits the account to its user_folders rows.
CREATE TABLE users (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    name          TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    password_hash TEXT,
    role          TEXT    NOT NULL CHECK (role IN ('owner', 'viewer')),
    disabled      INTEGER NOT NULL DEFAULT 0,
    all_folders   INTEGER NOT NULL DEFAULT 1,
    created_at    TEXT    NOT NULL,
    last_seen     TEXT
);
INSERT INTO users(id, name, role, created_at) VALUES (1, 'Owner', 'owner', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

CREATE TABLE user_folders (
    user_id   INTEGER NOT NULL REFERENCES users(id)   ON DELETE CASCADE,
    folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, folder_id)
);
CREATE INDEX ix_user_folders_folder ON user_folders(folder_id);

CREATE TABLE devices (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id      INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name         TEXT    NOT NULL,
    token_hash   TEXT    NOT NULL UNIQUE,
    created_at   TEXT    NOT NULL,
    last_seen    TEXT,
    last_address TEXT
);
CREATE INDEX ix_devices_user ON devices(user_id);

CREATE TABLE invites (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    code_hash  TEXT    NOT NULL UNIQUE,
    created_at TEXT    NOT NULL,
    expires_at TEXT    NOT NULL,
    used_at    TEXT
);
CREATE INDEX ix_invites_user ON invites(user_id);

-- The log outlives the account it names, so removing an account clears user_id instead of the rows.
CREATE TABLE sharing_log (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    at      TEXT    NOT NULL,
    user_id INTEGER REFERENCES users(id) ON DELETE SET NULL,
    kind    TEXT    NOT NULL,
    detail  TEXT    NOT NULL
);

CREATE TABLE progress (
    user_id     INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    position_s  REAL    NOT NULL DEFAULT 0,
    duration_s  REAL    NOT NULL DEFAULT 0,
    watched     INTEGER NOT NULL DEFAULT 0,
    last_played TEXT,
    PRIMARY KEY (user_id, file_id)
);
CREATE INDEX ix_progress_last_played ON progress(last_played);
CREATE INDEX ix_progress_file ON progress(file_id);

-- A title the user removed from Continue watching. played_at is the last_played of what the row showed then: the
-- title comes back once something is played after it. Progress itself is left alone.
CREATE TABLE continue_dismissed (
    user_id   INTEGER NOT NULL REFERENCES users(id)  ON DELETE CASCADE,
    title_id  INTEGER NOT NULL REFERENCES titles(id) ON DELETE CASCADE,
    played_at TEXT    NOT NULL,
    PRIMARY KEY (user_id, title_id)
);
CREATE INDEX ix_continue_dismissed_title ON continue_dismissed(title_id);

-- target_id points at a title or a file depending on target_kind, so it has no foreign key.
CREATE TABLE overrides (
    target_kind  TEXT    NOT NULL,
    target_id    INTEGER NOT NULL,
    display_name TEXT,
    hidden       INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (target_kind, target_id)
);

CREATE TABLE review_candidates (
    file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    tmdb_id INTEGER NOT NULL,
    kind    TEXT    NOT NULL,
    name    TEXT    NOT NULL,
    year    INTEGER,
    score   REAL    NOT NULL,
    PRIMARY KEY (file_id, kind, tmdb_id)
);

-- Small key/value facts about the stored data, such as the parser version the stored parses come from.
CREATE TABLE meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
