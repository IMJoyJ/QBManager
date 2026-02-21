# QBManager Lua API 文档

QBManager 向 Lua 脚本暴露了一个全局 `qb` 对象，用于与 qBittorrent WebUI 和本地文件系统交互。

## 全局对象

- **`qb`**: 包含所有 API 方法的主客户端对象。
- **`_G.LastError`**: 一个全局字符串变量，当 API 调用失败时，错误信息会写入此变量。
- **`print(...)`**: 重写为输出到宿主控制台，带有 `[Lua]` 前缀。

## 错误处理协议

所有 `qb` 方法遵循严格的错误处理模式：

1.  **成功**: 返回一个有效值（布尔值 `true`、数字、字符串或表）。**成功时绝不返回 `nil`。**
2.  **失败**: 返回 `nil`。具体的错误信息写入 `_G.LastError`。

**示例:**
```lua
local result = qb:MethodName()
if result == nil then
    print("Error: " .. _G.LastError)
    return 0 -- 向宿主报告失败
end
```

## 脚本返回代码

脚本执行结束时必须返回一个整数，以向宿主报告状态：

| 代码 | 含义 | 宿主行为 |
| :--- | :--- | :--- |
| **`1`** | **成功** | 脚本成功完成。继续执行下一个脚本。 |
| **`0`** | **重试** | 脚本失败但稍后可能成功（例如临时网络问题）。宿主将重试，直到达到配置的 `max_retry_attempts` 次数。 |
| **`2`** | **致命错误** | 脚本失败（例如逻辑错误）。宿主将记录错误并跳过此脚本，本轮不再重试。 |

---

## API 参考

### 种子管理

#### `qb:GetTorrents(hashes)`
获取所有种子或按哈希筛选。
- **参数**:
    - `hashes` (string, 可选): 竖线分隔的哈希列表（例如 "hash1|hash2"）。如果省略或为 nil，则返回所有种子。
- **返回值**: `table[]` (种子对象列表) 或 `nil`。
- **种子对象字段**:
    - `hash` (string): 种子哈希
    - `name` (string): 种子名称
    - `state` (string): 当前状态 (例如 "downloading", "pausedDL", "uploading")
    - `size` (number): 总大小（字节）
    - `progress` (number): 进度 (0.0 到 1.0)
    - `category` (string): 分类名称
    - `content_path` (string): 内容的绝对路径
    - `save_path` (string): 保存路径
    - ... 以及其他 WebUI 字段。

#### `qb:GetFiles(hash)`
返回特定种子的文件列表。
- **参数**: 
    - `hash` (string): 种子哈希。
- **返回值**: `table[]` (文件对象列表) 或 `nil`。
- **文件对象字段**: `name`, `size`, `progress`, `priority` 等。

#### `qb:DeleteTorrent(hash, deleteFiles)`
删除种子。
- **参数**:
    - `hash` (string): 种子哈希。
    - `deleteFiles` (bool): `true` 表示同时删除磁盘上的下载数据。
- **返回值**: `true` 或 `nil`。

#### `qb:PauseTorrents(hashes)`
暂停（停止）一个或多个种子。
- **参数**:
    - `hashes` (string): 竖线分隔的哈希列表（例如 "hash1|hash2"）或 "all"。
- **返回值**: `true` 或 `nil`。

#### `qb:ResumeTorrents(hashes, forceStart)`
恢复（开始）一个或多个种子。
- **参数**:
    - `hashes` (string): 竖线分隔的哈希列表（例如 "hash1|hash2"）或 "all"。
    - `forceStart` (bool|nil): `true` 表示强制下载，无视队列限制。默认为 `false`。
- **返回值**: `true` 或 `nil`。

#### `qb:SetForceStart(hashes, value)`
设置一个或多个种子的强制开始状态。
- **参数**:
    - `hashes` (string): 竖线分隔的哈希列表（例如 "hash1|hash2"）或 "all"。
    - `value` (bool): `true` 表示开启强制开始，`false` 表示关闭。
- **返回值**: `true` 或 `nil`。

#### `qb:AddTorrent(category, savePath, skipHashCheck, torrentUrl)`
通过 URL 添加种子。
- **参数**:
    - `category` (string): 分类名称。
    - `savePath` (string): 下载目标路径。
    - `skipHashCheck` (bool): 是否跳过哈希校验。
    - `torrentUrl` (string): .torrent 文件或磁力链接的 URL。
- **返回值**: `true` 或 `nil`。

#### `qb:AddTorrentFile(category, savePath, skipHashCheck, torrentFilePath)`
通过本地 `.torrent` 文件添加种子。
- **参数**:
    - `category` (string): 分类名称。
    - `savePath` (string): 下载目标路径。
    - `skipHashCheck` (bool): 是否跳过哈希校验。
    - `torrentFilePath` (string): `.torrent` 文件的本地绝对路径。
- **返回值**: `true` 或 `nil`。

#### `qb:ChangeCategory(hash, category)`
更改种子的分类。
- **参数**:
    - `hash` (string): 种子哈希。
    - `category` (string): 新的分类名称。
- **返回值**: `true` 或 `nil`。

#### `qb:GetTrackers(hash)`
获取种子的所有 Tracker。
- **参数**: `hash` (string)。
- **返回值**: 一个表数组，其中每个元素都是一个带有 `{ url, status, tier, msg, ... }` 的表，或返回 `nil`。

#### `qb:RemoveTrackers(hash, urls)`
移除种子下的某些 Tracker。
- **参数**: 
    - `hash` (string)。
    - `urls` (string): 竖线分隔的要移除的 Tracker URL（例如 "http://url1|http://url2"）。
- **返回值**: `true` 或 `nil`。

#### `qb:HasTracker(hash, pattern)`
检查是否有任何 Tracker URL 包含指定模式（大小写不敏感的子字符串匹配）。
- **参数**:
    - `hash` (string): 种子哈希。
    - `pattern` (string): 要检查的子字符串模式。
- **返回值**: `bool` (`true`/`false`) 或 `nil`。

#### `qb:SetLocation(hash, newPath)`
更改种子的保存位置。
- **参数**:
    - `hash` (string): 种子哈希。
    - `newPath` (string): 新的绝对路径。
- **返回值**: `true` 或 `nil`。

#### `qb:RenameFile(hash, oldPath, newPath)`
重命名种子内的文件（仅限重命名，不移动到种子外）。
- **参数**:
    - `hash` (string): 种子哈希。
    - `oldPath` (string): 文件当前的相对路径。
    - `newPath` (string): 新的相对路径。
- **返回值**: `true` 或 `nil`。

#### `qb:RenameFolder(hash, oldPath, newPath)`
重命名种子内的文件夹（仅限重命名，不移动到种子外）。
- **参数**:
    - `hash` (string): 种子哈希。
    - `oldPath` (string): 文件夹当前的相对路径。
    - `newPath` (string): 新的相对路径。
- **返回值**: `true` 或 `nil`。

### 文件系统操作（本地）

#### `qb:ExportTorrentFile(hash, destinationPath, overwrite)`
从 qBittorrent 导出 .torrent 文件到本地路径。
- **参数**:
    - `hash` (string): 种子哈希。
    - `destinationPath` (string): 保存 .torrent 文件的完整本地路径。
    - `overwrite` (bool): 如果文件存在是否覆盖（默认 `true`）。
- **返回值**: `true` 或 `nil`。

#### `qb:CopyTorrentFiles(hash, destinationPath, overwrite)`
将种子的实际内容文件复制到目标目录。
**注意**: 此函数具有类似事务的回滚机制。如果复制失败（例如磁盘已满），它会尝试删除刚刚复制的文件。
**安全**: 如果 `content_path == save_path`（种子结构损坏），将拒绝复制。
- **参数**:
    - `hash` (string): 种子哈希。
    - `destinationPath` (string): 目标目录路径。
    - `overwrite` (bool|nil): 三态覆盖控制：
        - `true`: 覆盖已存在的文件。
        - `false`: 跳过已存在的文件（将计数记录到 stderr）。
        - `nil` (默认): 如果目标文件已存在则报错。
- **返回值**: `true` 或 `nil`。

#### `qb:GetLocalFreeSpace(path)`
获取包含指定路径的驱动器上的可用空间。
- **参数**:
    - `path` (string): 有效的文件或目录路径。
- **返回值**: `number` (可用字节数) 或 `nil`。

#### `qb:PathExists(path)`
检查本地文件或目录是否存在。
- **参数**:
    - `path` (string): 要检查的路径。
- **返回值**: `bool` (`true`/`false`) 或 `nil`（如果检查失败，罕见）。

#### `qb:IsDirectory(path)`
检查路径是否为目录。
- **参数**:
    - `path` (string): 要检查的路径。
- **返回值**: `bool` (`true`/`false`) 或 `nil`（如果检查失败）。

#### `qb:MoveLocalFile(sourcePath, destinationPath)`
移动（重命名）本地文件。如有需要会自动创建目标目录。
- **参数**:
    - `sourcePath` (string): 源文件路径。
    - `destinationPath` (string): 目标文件路径。
- **返回值**: `true` 或 `nil`。

#### `qb:MoveLocalDirectory(sourcePath, destinationPath)`
移动（重命名）本地目录。如果目标已存在则失败。
- **参数**:
    - `sourcePath` (string): 源目录路径。
    - `destinationPath` (string): 目标目录路径。
- **返回值**: `true` 或 `nil`。

#### `qb:MergeLocalDirectory(sourceDir, destDir)`
将 `sourceDir` 的所有内容合并到 `destDir` 中。如有需要会自动创建 `destDir`。合并后删除 `sourceDir`。跳过目标中已存在的文件。
- **参数**:
    - `sourceDir` (string): 源目录路径。
    - `destDir` (string): 目标目录路径。
- **返回值**: `number` (移动的文件数) 或 `nil`。

### 工具函数

#### `qb:UrlEncode(str)`
对字符串进行 URL 编码。
- **返回值**: `string` 或 `nil`。

#### `qb:UrlDecode(str)`
对字符串进行 URL 解码。
- **返回值**: `string` 或 `nil`。

#### `qb:Sleep(seconds)`
暂停脚本执行指定的秒数。
- **参数**:
    - `seconds` (number): 要睡眠的秒数。
- **返回值**: `true` 或 `nil`。

#### `qb:AskYesNo(prompt)`
通过控制台 (stderr) 向用户显示 Yes/No 提示。阻塞直到用户响应。
- **参数**:
    - `prompt` (string): 要显示的问题。
- **返回值**: `bool` — 如果用户回答 `y`/`yes` 返回 `true`，否则返回 `false`。

---

### SQLite 数据库

> **注意**: 需要在 `config.json` 中设置 `database_path`。如果数据库文件不存在，将会自动创建。

#### `qb:DbExecute(sql, ...)`
执行非查询 SQL 语句（INSERT / UPDATE / DELETE / CREATE TABLE 等）。
- **参数**:
    - `sql` (string): SQL 语句。使用 `@p1`, `@p2`, ... 作为位置占位符。
    - `...` (any): 占位符的值，按顺序对应。
- **返回值**: `number` (受影响的行数) 或 `nil`。

**示例:**
```lua
-- 创建表
qb:DbExecute("CREATE TABLE IF NOT EXISTS log (id INTEGER PRIMARY KEY, msg TEXT, ts INTEGER)")

-- 带参数插入
local n = qb:DbExecute("INSERT INTO log (msg, ts) VALUES (@p1, @p2)", "hello", os.time())
print("Inserted " .. tostring(n) .. " row(s)")
```

#### `qb:DbQuery(sql, ...)`
执行 SELECT 查询。返回一个 Lua 表数组，其中每个元素都是一个行字典 `{ column_name = value }`。
- **参数**:
    - `sql` (string): SQL SELECT 语句。使用 `@p1`, `@p2`, ... 作为位置占位符。
    - `...` (any): 占位符的值，按顺序对应。
- **返回值**: `table[]` (行表列表) 或 `nil`。

**示例:**
```lua
local rows = qb:DbQuery("SELECT id, msg FROM log WHERE ts > @p1", 1700000000)
if rows then
    for i = 1, #rows do
        print(rows[i]["id"] .. ": " .. rows[i]["msg"])
    end
end
```

#### `qb:DbScalar(sql, ...)`
执行标量查询（返回第一行的第一列）。
- **参数**:
    - `sql` (string): SQL 查询。使用 `@p1`, `@p2`, ... 作为位置占位符。
    - `...` (any): 占位符的值，按顺序对应。
- **返回值**: `number`, `string`, 或 `nil`。

**示例:**
```lua
local count = qb:DbScalar("SELECT COUNT(*) FROM log")
print("Total rows: " .. tostring(count))
```

---

### 文件级操作

#### `qb:GetTorrentFiles(hash)` → `table[]`
返回属于该种子的文件列表。每个条目包含 `name` (相对于 `save_path` 的路径), `size`, `progress`, `priority` 等。
- **返回值**: `table[]` 或 `nil`。

#### `qb:DeleteLocalFile(path)`
删除单个本地文件。成功（或文件不存在）返回 `true`，错误返回 `nil`。

#### `qb:DeleteLocalDir(path)`
删除本地目录（**仅当为空时**）。删除成功返回 `true`，非空返回 `false`，错误返回 `nil`。

---

### 安全删除 (Lua 模块)

当多个种子共享同一目录中的文件时，使用 `scripts.safe_delete` 以避免删除其他种子仍需使用的文件。

```lua
local sd = require("scripts.safe_delete")
sd.SafeDelete(hash)  -- 检测文件重叠，仅删除非共享文件
```

所有基于 `remove_by_condition` 的模块均支持在配置中设置 `SafeDelete = true`：
```lua
local tes = require("scripts.tracker_error_seeding")
return tes.Run({
    GetCandidates = function() return qb:GetTorrents() end,
    SafeDelete = true,  -- 启用文件重叠检测
})
```
