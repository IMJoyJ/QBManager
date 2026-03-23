--[[
    add_torrent_from_rss_sqlite.lua
    从 RSS Feed 添加新种子（SQLite 持久化去重）模块 (Module)

    适用场景：下载后立即停止做种并删除种子（不删文件）时，
    qBittorrent 列表中不再保留该记录，普通列表去重会导致重复下载，
    故使用 SQLite 持久化已处理的 hash。

    数据流：
      1. 确保 SQLite 表存在；按 MaxSaveDays 清理过期记录
      2. 拉取 RSS → 获取 item 列表
      3. 获取 qBittorrent 当前种子列表（内存去重，兜底）
      4. 遍历每个 item：
         a. hash 在 SQLite 中已记录 → 跳过
         b. hash 在 qBittorrent 中已存在 → 跳过
         c. Filter 拒绝 → 跳过
         d. DownloadFile → AddTorrentFile → 删除临时文件
         e. 成功后将 hash 写入 SQLite
      5. 汇总

    提供 Run(config) 函数：
    config = {
        TableName   = "rss_agsvpt_downloaded",                    -- SQLite 表名（必须）
        Url         = "https://pt.example.cn/rss.php?passkey=xxx",-- RSS 地址（必须）
        Category    = "agsvpt",                                    -- qB 分类（必须）
        SavePath    = "D:\\Downloads\\agsvpt",                     -- 保存路径（必须）
        Filter      = function(item) return true end,             -- (可选) 过滤函数
        TempDir     = os.getenv("TEMP") or "C:\\Temp",            -- (可选) 临时目录
        MaxSaveDays = 30,  -- (可选) 记录保留天数；负数=永不自动清理；默认 30
    }
]]

local M = {}

function M.Run(config)
    print("========================================")
    print("  add_torrent_from_rss_sqlite.lua 开始执行")
    print("========================================")

    local tableName   = config.TableName
    local url         = config.Url
    local category    = config.Category
    local savePath    = config.SavePath
    local filter      = config.Filter
    local tempDir     = config.TempDir or (os.getenv("TEMP") or "C:\\Temp")
    local maxSaveDays = (config.MaxSaveDays ~= nil) and config.MaxSaveDays or 30

    -- ── 1. 建表 ───────────────────────────────────────────────────────
    local createSql = string.format([[
        CREATE TABLE IF NOT EXISTS %s (
            hash       TEXT PRIMARY KEY,
            title      TEXT,
            added_at   INTEGER NOT NULL
        )
    ]], tableName)
    local ok = qb:DbExecute(createSql)
    if ok == nil then
        print("[ERROR] 创建 SQLite 表失败: " .. tostring(_G.LastError))
        return 0
    end

    -- ── 1b. 清理过期记录 ──────────────────────────────────────────────
    if maxSaveDays >= 0 then
        local cutoff = os.time() - maxSaveDays * 86400
        local delSql = string.format(
            "DELETE FROM %s WHERE added_at < @p1", tableName)
        local delCount = qb:DbExecute(delSql, cutoff)
        if delCount and delCount > 0 then
            print(string.format("[INFO] 已清理 %d 条超过 %d 天的旧记录", delCount, maxSaveDays))
        end
    end

    -- ── 2. 拉取 RSS ───────────────────────────────────────────────────
    print(string.format("[INFO] 获取 RSS: %s", url))
    local items = qb:GetRSS(url)
    if not items then
        print("[ERROR] 获取 RSS 失败: " .. tostring(_G.LastError))
        return 0
    end
    print(string.format("[INFO] RSS 共 %d 条 item", #items))

    if #items == 0 then
        print("[INFO] RSS 为空，无需处理。")
        return 1
    end

    -- ── 3. 获取 qBittorrent 当前种子（内存去重兜底）──────────────────
    local torrents = qb:GetTorrents()
    if not torrents then
        print("[ERROR] 获取种子列表失败: " .. tostring(_G.LastError))
        return 0
    end

    local qbExisting = {}
    for i = 1, #torrents do
        local h = torrents[i]["hash"]
        if h then qbExisting[string.lower(h)] = true end
    end
    print(string.format("[INFO] qBittorrent 当前种子数: %d", #torrents))

    -- ── 4. 遍历 item ──────────────────────────────────────────────────
    local addedCount    = 0
    local skippedSqlite = 0
    local skippedQB     = 0
    local skippedFilter = 0

    for i = 1, #items do
        local item = items[i]
        local title        = item["title"]        or "(无标题)"
        local hash         = string.lower(item["hash"] or "")
        local enclosureUrl = item["enclosure_url"] or ""

        -- 4a. SQLite 去重
        if hash ~= "" then
            local countSql = string.format(
                "SELECT COUNT(*) FROM %s WHERE hash = @p1", tableName)
            local cnt = qb:DbScalar(countSql, hash)
            if cnt and cnt > 0 then
                print(string.format("[SKIP] SQLite 已记录: %s", title))
                skippedSqlite = skippedSqlite + 1
                goto continue
            end
        end

        -- 4b. qBittorrent 列表兜底去重
        if hash ~= "" and qbExisting[hash] then
            print(string.format("[SKIP] qB 中已存在: %s", title))
            skippedQB = skippedQB + 1
            goto continue
        end

        -- 4c. 过滤
        if filter and not filter(item) then
            print(string.format("[SKIP] 过滤器拒绝: %s", title))
            skippedFilter = skippedFilter + 1
            goto continue
        end

        -- 4d. 下载并添加
        if enclosureUrl == "" then
            print(string.format("[WARN] 缺少 enclosure_url，跳过: %s", title))
            goto continue
        end

        do
            local safeName = (hash ~= "") and hash or tostring(i)
            local tmpPath  = tempDir .. "\\" .. safeName .. ".torrent"

            print(string.format("[ADD] 下载种子: %s", title))
            local dlOk = qb:DownloadFile(enclosureUrl, tmpPath)
            if not dlOk then
                print(string.format("[ERROR] 下载失败: %s — %s", title, tostring(_G.LastError)))
                goto continue
            end

            local addOk = qb:AddTorrentFile(category, savePath, false, tmpPath)
            qb:DeleteLocalFile(tmpPath)

            if not addOk then
                print(string.format("[ERROR] 添加失败: %s — %s", title, tostring(_G.LastError)))
                goto continue
            end

            -- 4e. 写入 SQLite
            if hash ~= "" then
                local insSql = string.format(
                    "INSERT OR IGNORE INTO %s (hash, title, added_at) VALUES (@p1, @p2, @p3)",
                    tableName)
                qb:DbExecute(insSql, hash, title, os.time())
                qbExisting[hash] = true  -- 同批次内去重
            end

            print(string.format("[ADD] ✓ 已添加: %s", title))
            addedCount = addedCount + 1
        end

        ::continue::
    end

    -- ── 5. 汇总 ──────────────────────────────────────────────────────
    print(string.format(
        "[INFO] 完成：新增 %d，跳过（SQLite） %d，跳过（qB） %d，跳过（过滤） %d",
        addedCount, skippedSqlite, skippedQB, skippedFilter))
    return 1
end

return M
