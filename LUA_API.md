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

#### `qb:AddTorrent(category, savePath, skipHashCheck, torrentUrl)`
Adds a torrent from a URL.
- **Parameters**:
    - `category` (string): Category name.
    - `savePath` (string): Download destination path.
    - `skipHashCheck` (bool): Whether to skip hash checking.
    - `torrentUrl` (string): URL to the .torrent file or magnet link.
- **Returns**: `true` or `nil`.

#### `qb:ChangeCategory(hash, category)`
Changes the category of a torrent.
- **Parameters**:
    - `hash` (string): Torrent hash.
    - `category` (string): New category name.
- **Returns**: `true` or `nil`.

#### `qb:GetTrackers(hash)`
Returns a list of trackers for a specific torrent.
- **Parameters**:
    - `hash` (string): Torrent hash.
- **Returns**: `table[]` (list of tracker objects) or `nil`.
- **Tracker Object Fields**: `url`, `status`, `msg`, etc.

#### `qb:HasTracker(hash, pattern)`
Checks if any tracker URL contains the specified pattern (case-insensitive substring match).
- **Parameters**:
    - `hash` (string): Torrent hash.
    - `pattern` (string): Substring pattern to check.
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
- **Parameters**:
    - `hash` (string): Torrent hash.
    - `destinationPath` (string): Destination directory path.
    - `overwrite` (bool): Whether to overwrite existing files (default `false`).
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
