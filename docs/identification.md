# Identification

MediaBoard reads the whole path, folder names included, and handles release noise, fansub names and episode ranges.

1. A `[tmdbid-123]` or `[imdbid-tt123]` tag in a file or folder name, or an `.nfo` file with an ID, is a certain match.
2. Otherwise the path is parsed: release noise (resolution, codec, source, group, language tags) is stripped, then the year,
   `S01E01`, `S01E01-E02`, `1x02` and season folders are read. A bare episode number counts when the folder is one season.
3. TMDB is searched, retrying with shorter titles and without the year when nothing fits.
4. Candidates are scored on title similarity, year, type and popularity, and checked against the episode list, episode titles
   and the file's duration (read with `ffprobe` when it's available).
5. A confident match is accepted. Anything else goes to **Needs review** with candidates and a search box, and your choice is
   remembered for the other episodes of the same show folder.

One file can hold several episodes, and a movie inside a show folder is identified as a movie. Folders with no video, partial
downloads and sample files are ignored, and the Folders view says why.

New files get a title from their name while offline and are identified when the connection returns.
