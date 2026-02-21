--[[
    constant_size_seeding.lua
    功能：体积恒定做种模块 (Module)
    
    用于某类种子【只能占固定体积，超出则删除到那个体积之下】。
    不移动种子，只备份。不检查任何盘符剩余空间。
    备份过的种子 hash 存入 SQLite（每个调用方独立建表）。
    删除前确保已备份，未备份则先备份再删除。
    
    提供 Run(config) 函数：
    config = {
        TableName        = "xxx_backup",         -- SQLite 表名（调用方独立）
        MaxSize          = 500 * 1024^3,         -- 最大总体积 (bytes)
        GetCandidates    = function() -> {},     -- 返回所有相关种子（需包含 size, added_on）
        SortForDelete    = function(a, b) -> bool, -- (可选) 删除优先级排序，默认按 added_on 升序
        EnableBackup     = false,                -- (可选) 是否启用备份，默认 false
        PreBackup        = function(t) -> t,     -- (可选) 预处理（如重命名），仅 EnableBackup 时生效
        GetBackupPath    = function(t) -> "",    -- 返回备份目标路径，仅 EnableBackup 时需要
        Overwrite        = nil,                   -- (可选) 备份覆盖策略: true=覆盖, false=跳过, nil=报错(默认)
    }
]]

local M = {}

function M.Run(config)
    print("========================================")
    print("  constant_size_seeding.lua 开始执行")
    print("  Table: " .. (config.TableName or "N/A"))
    print(string.format("  MaxSize: %.2f GB", (config.MaxSize or 0) / (1024^3)))
    print("========================================")

    local enableBackup = config.EnableBackup or false

    -- 1. 确保 SQLite 表存在（仅备份模式）
    if enableBackup then
        local createSql = string.format(
            "CREATE TABLE IF NOT EXISTS %s (hash TEXT PRIMARY KEY, name TEXT, backed_up_at INTEGER)",
            config.TableName
        )
        local ok = qb:DbExecute(createSql)
        if ok == nil then
            print("[ERROR] 创建备份记录表失败: " .. tostring(_G.LastError))
            return 0
        end
    end

    -- 2. 获取所有候选种子
    local candidates = config.GetCandidates()
    if candidates == nil then
        print("[ERROR] 获取种子列表失败")
        return 0
    end

    if #candidates == 0 then
        print("[INFO] 没有符合条件的种子，任务完成。")
        return 1
    end

    -- 3. 计算总体积
    local totalSize = 0
    for i = 1, #candidates do
        totalSize = totalSize + (candidates[i]["size"] or 0)
    end

    print(string.format("[INFO] 当前种子数: %d, 总体积: %.2f GB, 限制: %.2f GB",
        #candidates, totalSize / (1024^3), config.MaxSize / (1024^3)))

    -- 4. 确保所有种子都已备份（仅备份模式）
    if enableBackup then
        for i = 1, #candidates do
            local t = candidates[i]
            local hash = t["hash"]
            local name = t["name"]

            -- 检查是否已备份
            local checkSql = string.format(
                "SELECT COUNT(*) FROM %s WHERE hash = @p1",
                config.TableName
            )
            local count = qb:DbScalar(checkSql, hash)
            if count == nil then
                print("[ERROR] 查询备份记录失败: " .. tostring(_G.LastError))
                return 0
            end

            if count == 0 then
                -- 未备份，执行备份
                print(string.format("[BACKUP] 备份种子: %s", name))

                -- 预处理
                if config.PreBackup then
                    local newT = config.PreBackup(t)
                    if newT == nil then
                        print("[ERROR] PreBackup 失败，停止处理。请检查种子状态后手动处理。")
                        return 2
                    end
                    t = newT
                end

                -- 备份
                if config.GetBackupPath then
                    local destDir = config.GetBackupPath(t)
                    if destDir then
                        print(string.format("[INFO] 备份到: %s", destDir))
                        local success = qb:CopyTorrentFiles(hash, destDir, config.Overwrite)
                        if not success then
                            print("[ERROR] 备份失败: " .. tostring(_G.LastError))
                            return 0
                        end
                    end
                end

                -- 记录备份
                local insertSql = string.format(
                    "INSERT OR IGNORE INTO %s (hash, name, backed_up_at) VALUES (@p1, @p2, @p3)",
                    config.TableName
                )
                local ins = qb:DbExecute(insertSql, hash, name, os.time())
                if ins == nil then
                    print("[ERROR] 记录备份失败: " .. tostring(_G.LastError))
                    return 0
                end

                print(string.format("[BACKUP] ✓ 备份完成: %s", name))
            end
        end
    end

    -- 5. 如果总体积未超限，完成
    if totalSize <= config.MaxSize then
        print("[INFO] 总体积未超限，无需删除。任务完成。")
        return 1
    end

    -- 6. 超限，按优先级排序后逐个删除，直到体积降到限制以下
    -- 默认按 added_on 升序（最早添加的优先删除）
    local sortFunc = config.SortForDelete or function(a, b)
        return (a["added_on"] or 0) < (b["added_on"] or 0)
    end
    table.sort(candidates, sortFunc)

    print(string.format("[DELETE] 需要释放: %.2f GB",
        (totalSize - config.MaxSize) / (1024^3)))

    for i = 1, #candidates do
        if totalSize <= config.MaxSize then
            break
        end

        local t = candidates[i]
        local hash = t["hash"]
        local name = t["name"]
        local size = t["size"] or 0

        -- 删除前再次确认已备份（双重检查，仅备份模式）
        if enableBackup then
            local checkSql = string.format(
                "SELECT COUNT(*) FROM %s WHERE hash = @p1",
                config.TableName
            )
            local count = qb:DbScalar(checkSql, hash)
            if count == nil or count == 0 then
                -- 安全起见，如果发现未备份（不应该到这里），跳过
                print(string.format("[WARN] 种子未备份，跳过删除: %s", name))
                goto continue_delete
            end
        end

        do
            print(string.format("[DELETE] 删除种子: %s (%.2f GB)", name, size / (1024^3)))
            local delOk = qb:DeleteTorrent(hash, true)
            if not delOk then
                print("[ERROR] 删除失败: " .. tostring(_G.LastError))
                return 0
            end
            totalSize = totalSize - size
            print(string.format("[DELETE] ✓ 剩余: %.2f GB / %.2f GB",
                totalSize / (1024^3), config.MaxSize / (1024^3)))
        end

        ::continue_delete::
    end

    print("[INFO] 体积控制完成。")
    return 1
end

return M
