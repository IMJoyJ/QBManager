# QBManager Lua API Documentation

QBManager exposes a global `qb` object to Lua scripts for interacting with the qBittorrent WebUI and the local file system.

## Global Objects

- **`qb`**: The main client object containing all API methods.
- **`_G.LastError`**: A global string variable where error messages are written when an API call fails.
- **`print(...)`**: Overridden to output to the host console with `[Lua]` prefix.

## Error Handling Protocol

All `qb` methods follow a strict error handling pattern:

1.  **Success**: Returns a valid value (boolean `true`, number, string, or table). **Never returns `nil` on success.**
2.  **Failure**: Returns `nil`. The specific error message is written to `_G.LastError`.

**Example:**
```lua
local result = qb:MethodName()
if result == nil then
    print("Error: " .. _G.LastError)
    return 0 -- Signal failure to host
end
```

## Script Return Codes

Scripts must return an integer at the end of execution to signal status to the host:

| Code | Meaning | Host Behavior |
| :--- | :--- | :--- |
| **`1`** | **Success** | Script completed successfully. Proceed to next script. |
| **`0`** | **Retry** | Script failed but might succeed later (e.g., temporary network issue). Host will retry up to configured `max_retry_attempts`. |
| **`2`** | **Fatal** | Script failed successfully (e.g., logic error). Host will log error and skip to next script without retrying. |

---

## API Reference

### Torrent Management

#### `qb:GetTorrents(hashes)`
Get all torrents or filtered by hash.
- **Parameters**:
    - `hashes` (string, optional): Pipe-separated list of hashes (e.g. "hash1|hash2"). If omitted or nil, returns all torrents.
- **Returns**: `table[]` (list of torrent objects) or `nil`.
- **Torrent Object Fields**:
    - `hash` (string): Torrent hash
    - `name` (string): Torrent name
    - `state` (string): Current state (e.g., "downloading", "pausedDL", "uploading")
    - `size` (number): Total size in bytes
    - `progress` (number): Progress (0.0 to 1.0)
    - `category` (string): Category name
    - `content_path` (string): Absolute path to content
    - `save_path` (string): Save path
    - ... and other WebUI fields.

#### `qb:GetFiles(hash)`
Returns a list of files for a specific torrent.
- **Parameters**: 
    - `hash` (string): The torrent hash.
- **Returns**: `table[]` (List of file objects) or `nil`.
- **File Object Fields**: `name`, `size`, `progress`, `priority`, etc.

#### `qb:DeleteTorrent(hash, deleteFiles)`
Deletes a torrent.
- **Parameters**:
    - `hash` (string): Torrent hash.
    - `deleteFiles` (bool): `true` to also delete downloaded data on disk.
- **Returns**: `true` or `nil`.

#### `qb:PauseTorrents(hashes)`
Pauses (stops) one or more torrents.
- **Parameters**:
    - `hashes` (string): Pipe-separated list of hashes (e.g., "hash1|hash2") or "all".
- **Returns**: `true` or `nil`.

#### `qb:ResumeTorrents(hashes, forceStart)`
Resumes (starts) one or more torrents.
- **Parameters**:
    - `hashes` (string): Pipe-separated list of hashes (e.g., "hash1|hash2") or "all".
    - `forceStart` (bool|nil): `true` to force start, bypassing queue limits. Defaults to `false`.
- **Returns**: `true` or `nil`.

#### `qb:SetForceStart(hashes, value)`
Sets force start state for one or more torrents.
- **Parameters**:
    - `hashes` (string): Pipe-separated list of hashes (e.g., "hash1|hash2") or "all".
    - `value` (bool): `true` to force start, `false` to disable.
- **Returns**: `true` or `nil`.

#### `qb:AddTorrent(category, savePath, skipHashCheck, torrentUrl)`
Adds a torrent from a URL.
- **Parameters**:
    - `category` (string): Category name.
    - `savePath` (string): Download destination path.
    - `skipHashCheck` (bool): Whether to skip hash checking.
    - `torrentUrl` (string): URL to the .torrent file or magnet link.
- **Returns**: `true` or `nil`.

#### `qb:AddTorrentFile(category, savePath, skipHashCheck, torrentFilePath)`
Adds a torrent from a local `.torrent` file.
- **Parameters**:
    - `category` (string): Category name.
    - `savePath` (string): Download destination path.
    - `skipHashCheck` (bool): Whether to skip hash checking.
    - `torrentFilePath` (string): Absolute local path to the `.torrent` file.
- **Returns**: `true` or `nil`.

#### `qb:ChangeCategory(hash, category)`
Changes the category of a torrent.
- **Parameters**:
    - `hash` (string): Torrent hash.
    - `category` (string): New category name.
- **Returns**: `true` or `nil`.

#### `qb:GetTrackers(hash)`
Gets all trackers for a torrent.
- **Parameters**: `hash` (string).
- **Returns**: A table array where each element is a table with `{ url, status, tier, msg, ... }`, or `nil`.

#### `qb:AddTrackers(hash, urls)`
Adds trackers to a torrent.
- **Parameters**: 
    - `hash` (string).
    - `urls` (string): Trackers to add, separated by `%0A` or `\n`.
- **Returns**: `true` or `nil`.

#### `qb:RemoveTrackers(hash, urls)`
Removes trackers from a torrent.
- **Parameters**: 
    - `hash` (string).
    - `urls` (string): Pipe-separated list of tracker URLs to remove (e.g., "http://url1|http://url2").
- **Returns**: `true` or `nil`.

#### `qb:HasTracker(hash, pattern)`
Checks if a torrent has a tracker matching the pattern (substring match).
- **Parameters**:
    - `hash` (string).
    - `pattern` (string).
- **Returns**: `bool` (`true`/`false`) or `nil`.

#### `qb:SetLocation(hash, newPath)`
Changes the save location of a torrent.
- **Parameters**:
    - `hash` (string): Torrent hash.
    - `newPath` (string): New absolute path.
- **Returns**: `true` or `nil`.

#### `qb:RenameFile(hash, oldPath, newPath)`
Renames a file strictly within the torrent.
- **Parameters**:
    - `hash` (string): Torrent hash.
    - `oldPath` (string): Current relative path of the file.
    - `newPath` (string): New relative path.
- **Returns**: `true` or `nil`.

#### `qb:RenameFolder(hash, oldPath, newPath)`
Renames a folder strictly within the torrent.
- **Parameters**:
    - `hash` (string): Torrent hash.
    - `oldPath` (string): Current relative path of the folder.
    - `newPath` (string): New relative path.
- **Returns**: `true` or `nil`.

### File System Operations (Local)

#### `qb:ExportTorrentFile(hash, destinationPath, overwrite)`
Exports the .torrent file from qBittorrent to a local path.
- **Parameters**:
    - `hash` (string): Torrent hash.
    - `destinationPath` (string): Full local path to save the .torrent file.
    - `overwrite` (bool): Whether to overwrite if file exists (default `true`).
- **Returns**: `true` or `nil`.

#### `qb:CopyTorrentFiles(hash, destinationPath, overwrite)`
Copies the actual content files of a torrent to a destination directory. 
**Note**: This function has transaction-like rollback. If the copy fails (e.g., disk full), it attempts to delete any files it just copied.
**Safety**: Refuses to copy if `content_path == save_path` (broken torrent structure).
- **Parameters**:
    - `hash` (string): Torrent hash.
    - `destinationPath` (string): Destination directory path.
    - `overwrite` (bool|nil): Tri-state overwrite control:
        - `true`: Overwrite existing files.
        - `false`: Skip existing files (log count to stderr).
        - `nil` (default): Error if destination file already exists.
- **Returns**: `true` or `nil`.

#### `qb:GetLocalFreeSpace(path)`
Gets the available free space on the drive containing the specified path.
- **Parameters**:
    - `path` (string): A valid file or directory path.
- **Returns**: `number` (Bytes available) or `nil`.

#### `qb:PathExists(path)`
Checks if a file or directory exists locally.
- **Parameters**:
    - `path` (string): Path to check.
- **Returns**: `bool` (`true`/`false`) or `nil` if check failed (rare).

#### `qb:IsDirectory(path)`
Checks if path is a directory.
- **Parameters**:
    - `path` (string): Path to check.
- **Returns**: `bool` (`true`/`false`) or `nil` if check failed.

#### `qb:MoveLocalFile(sourcePath, destinationPath)`
Moves (renames) a local file. Creates destination directory if needed.
- **Parameters**:
    - `sourcePath` (string): Source file path.
    - `destinationPath` (string): Destination file path.
- **Returns**: `true` or `nil`.

#### `qb:MoveLocalDirectory(sourcePath, destinationPath)`
Moves (renames) a local directory. Fails if destination already exists.
- **Parameters**:
    - `sourcePath` (string): Source directory path.
    - `destinationPath` (string): Destination directory path.
- **Returns**: `true` or `nil`.

#### `qb:MergeLocalDirectory(sourceDir, destDir)`
Merges all contents of `sourceDir` into `destDir`. Creates `destDir` if needed. Removes `sourceDir` after merge. Skips files that already exist in destination.
- **Parameters**:
    - `sourceDir` (string): Source directory path.
    - `destDir` (string): Destination directory path.
- **Returns**: `number` (files moved) or `nil`.

### Utility

#### `qb:UrlEncode(str)`
URL-encodes a string.
- **Returns**: `string` or `nil`.

#### `qb:UrlDecode(str)`
URL-decodes a string.
- **Returns**: `string` or `nil`.

#### `qb:Sleep(seconds)`
Pauses script execution for the specified number of seconds.
- **Parameters**:
    - `seconds` (number): Seconds to sleep.
- **Returns**: `true` or `nil`.

#### `qb:AskYesNo(prompt)`
Prompts the user with a yes/no question via console (stderr). Blocks until user responds.
- **Parameters**:
    - `prompt` (string): The question to display.
- **Returns**: `bool` — `true` if user answers `y`/`yes`, `false` otherwise.

---

### SQLite Database
Used for persistent storage. Can be used to track which files have been backed up or which torrents have been processed.

> **Note**: Requires `database_path` to be set in `config.json`. The database file will be created automatically if it does not exist.

#### `qb:DbExecute(sql, ...)`
Execute a non-query SQL statement (INSERT / UPDATE / DELETE / CREATE TABLE, etc.).
- **Parameters**:
    - `sql` (string): SQL statement. Use `@p1`, `@p2`, ... as positional placeholders.
    - `...` (any): Values for the placeholders, in order.
- **Returns**: `number` (affected rows) or `nil`.

**Example:**
```lua
-- Create table
qb:DbExecute("CREATE TABLE IF NOT EXISTS log (id INTEGER PRIMARY KEY, msg TEXT, ts INTEGER)")

-- Insert with parameters
local n = qb:DbExecute("INSERT INTO log (msg, ts) VALUES (@p1, @p2)", "hello", os.time())
print("Inserted " .. tostring(n) .. " row(s)")
```

#### `qb:DbQuery(sql, ...)`
Execute a SELECT query. Returns a Lua table array where each element is a row dictionary `{ column_name = value }`.
- **Parameters**:
    - `sql` (string): SQL SELECT statement. Use `@p1`, `@p2`, ... as positional placeholders.
    - `...` (any): Values for the placeholders, in order.
- **Returns**: `table[]` (list of row tables) or `nil`.

**Example:**
```lua
local rows = qb:DbQuery("SELECT id, msg FROM log WHERE ts > @p1", 1700000000)
if rows then
    for i = 1, #rows do
        print(rows[i]["id"] .. ": " .. rows[i]["msg"])
    end
end
```

#### `qb:DbScalar(sql, ...)`
Execute a scalar query (returns the first column of the first row).
- **Parameters**:
    - `sql` (string): SQL query. Use `@p1`, `@p2`, ... as positional placeholders.
    - `...` (any): Values for the placeholders, in order.
- **Returns**: `number`, `string`, or `nil`.

**Example:**
```lua
local count = qb:DbScalar("SELECT COUNT(*) FROM log")
print("Total rows: " .. tostring(count))
```

---

### File-Level Operations

#### `qb:GetTorrentFiles(hash)` → `table[]`
Returns the list of files belonging to a torrent. Each entry has `name` (path relative to `save_path`), `size`, `progress`, `priority`, etc.
- **Returns**: `table[]` or `nil`.

#### `qb:DeleteLocalFile(path)`
Deletes a single local file. Returns `true` on success (or if file doesn't exist), `nil` on error.

#### `qb:DeleteLocalDir(path)`
Deletes a local directory **only if empty**. Returns `true` if deleted, `false` if not empty, `nil` on error.

---

### Safe Delete (Lua Module)

When torrents share files in the same directory, use `scripts.safe_delete` to avoid deleting files still needed by other torrents.

```lua
local sd = require("scripts.safe_delete")
sd.SafeDelete(hash)  -- detects file overlap, only deletes non-shared files
```

All `remove_by_condition`-based modules support `SafeDelete = true` in their config:
```lua
local tes = require("scripts.tracker_error_seeding")
return tes.Run({
    GetCandidates = function() return qb:GetTorrents() end,
    SafeDelete = true,  -- enable file-overlap detection
})
```
