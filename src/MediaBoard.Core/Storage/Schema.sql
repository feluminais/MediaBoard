-- Schema v3 for a new database. Applied once by LibraryDatabase when PRAGMA user_version is 0; an older
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

CREATE TABLE progress (
    file_id     INTEGER PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
    position_s  REAL    NOT NULL DEFAULT 0,
    duration_s  REAL    NOT NULL DEFAULT 0,
    watched     INTEGER NOT NULL DEFAULT 0,
    last_played TEXT
);
CREATE INDEX ix_progress_last_played ON progress(last_played);

-- A title the user removed from Continue watching. played_at is the last_played of what the row showed then: the
-- title comes back once something is played after it. Progress itself is left alone.
CREATE TABLE continue_dismissed (
    title_id  INTEGER PRIMARY KEY REFERENCES titles(id) ON DELETE CASCADE,
    played_at TEXT    NOT NULL
);

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
