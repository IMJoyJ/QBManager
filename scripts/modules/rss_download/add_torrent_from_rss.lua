--[[
    add_torrent_from_rss.lua
    从 RSS Feed 添加新种子模块 (Module)

    流程：
      1. 拉取 RSS → 获取 item 列表
      2. 获取 qBittorrent 当前所有种子，建立 hash 集合用于去重
      3. 遍历每个 item：
         a. 若 hash 已在 qBittorrent 中 → 跳过（已存在）
         b. 若提供了 filter 函数且 filter(item) 返回 false → 跳过
         c. 下载 .torrent 到临时文件 → AddTorrentFile → 删除临时文件
      4. 返回成功添加的数量

    提供 Run(config) 函数：
    config = {
        Url      = "https://pt.example.cn/rss.php?passkey=xxx",  -- RSS 地址
        Category = "agsvpt",                                      -- qB 分类
        SavePath = "D:\\Downloads\\agsvpt",                       -- 保存路径
        Filter   = function(item) return true end,                -- (可选) 过滤函数
        TempDir  = os.getenv("TEMP") or "C:\\Temp",              -- (可选) 临时目录
    }
]]

local M = {}

function M.Run(config)
    print("========================================")
    print("  add_torrent_from_rss.lua 开始执行")
    print("========================================")

    local url      = config.Url
    local category = config.Category
    local savePath = config.SavePath
    local filter   = config.Filter      -- 可为 nil
    local tempDir  = config.TempDir or (os.getenv("TEMP") or "C:\\Temp")

    -- ── 1. 拉取 RSS ───────────────────────────────────────────────────
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

    -- ── 2. 获取现有种子 hash 集合（去重用）────────────────────────────
    local torrents = qb:GetTorrents()
    if not torrents then
        print("[ERROR] 获取种子列表失败: " .. tostring(_G.LastError))
        return 0
    end

    local existing = {}
    for i = 1, #torrents do
        local h = torrents[i]["hash"]
        if h then
            existing[string.lower(h)] = true
        end
    end
    print(string.format("[INFO] qBittorrent 当前种子数: %d", #torrents))

    -- ── 3. 遍历 item，添加新种子 ──────────────────────────────────────
    local addedCount   = 0
    local skippedExist = 0
    local skippedFilter = 0

    for i = 1, #items do
        local item = items[i]
        local title        = item["title"]        or "(无标题)"
        local hash         = string.lower(item["hash"] or "")
        local enclosureUrl = item["enclosure_url"] or ""

        -- 3a. 去重
        if hash ~= "" and existing[hash] then
            print(string.format("[SKIP] 已存在: %s", title))
            skippedExist = skippedExist + 1
            goto continue
        end

        -- 3b. 过滤
        if filter and not filter(item) then
            print(string.format("[SKIP] 过滤器拒绝: %s", title))
            skippedFilter = skippedFilter + 1
            goto continue
        end

        -- 3c. 下载并添加
        if enclosureUrl == "" then
            print(string.format("[WARN] item 缺少 enclosure_url，跳过: %s", title))
            goto continue
        end

        do
            -- 临时文件路径（用 hash 或序号命名，避免冲突）
            local safeName = (hash ~= "") and hash or tostring(i)
            local tmpPath = tempDir .. "\\" .. safeName .. ".torrent"

            print(string.format("[ADD] 下载种子: %s", title))
            local dlOk = qb:DownloadFile(enclosureUrl, tmpPath)
            if not dlOk then
                print(string.format("[ERROR] 下载失败: %s — %s", title, tostring(_G.LastError)))
                goto continue
            end

            local addOk = qb:AddTorrentFile(category, savePath, false, tmpPath)

            -- 无论添加成功与否都删除临时文件
            qb:DeleteLocalFile(tmpPath)

            if not addOk then
                print(string.format("[ERROR] 添加失败: %s — %s", title, tostring(_G.LastError)))
                goto continue
            end

            print(string.format("[ADD] ✓ 已添加: %s", title))
            addedCount = addedCount + 1

            -- 添加到 existing，避免同一批次内重复（RSS 偶尔有重复条目）
            if hash ~= "" then
                existing[hash] = true
            end
        end

        ::continue::
    end

    -- ── 4. 汇总 ──────────────────────────────────────────────────────
    print(string.format(
        "[INFO] 完成：新增 %d，跳过（已存在） %d，跳过（过滤） %d",
        addedCount, skippedExist, skippedFilter))
    return 1
end

return M
