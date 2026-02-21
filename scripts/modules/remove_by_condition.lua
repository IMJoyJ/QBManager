--[[
    remove_by_condition.lua
    功能：按条件删除种子模块 (Module)
    
    筛选种子 → 可选备份 → 满足条件则删除。
    与 constant_size_seeding 类似，但不用排序或限制体积，
    而是用 ShouldRemove 函数判断每个种子是否需要删除。
    
    提供 Run(config) 函数：
    config = {
        TableName        = "xxx_backup",         -- SQLite 表名（调用方独立），仅 EnableBackup 时需要
        GetCandidates    = function() -> {},     -- 返回所有相关种子
        ShouldRemove     = function(t) -> bool,  -- 返回 true 则删除该种子
        EnableBackup     = false,                -- (可选) 是否启用备份，默认 false
        SafeDelete       = false,                -- (可选) 安全删除：检测文件重叠，仅删非共享文件
        PreBackup        = function(t) -> t,     -- (可选) 预处理，仅 EnableBackup 时生效
        GetBackupPath    = function(t) -> "",    -- 返回备份目标路径，仅 EnableBackup 时需要
        Overwrite        = nil,                   -- (可选) 备份覆盖策略: true=覆盖, false=跳过, nil=报错(默认)
    }
]]

local sd = require("scripts.modules.safe_delete")

local M = {}

function M.Run(config)
    print("========================================")
    print("  remove_by_condition.lua 开始执行")
    print("========================================")

    local enableBackup = config.EnableBackup or false
    local safeDelete = config.SafeDelete or false

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

    print(string.format("[INFO] 候选种子数: %d", #candidates))

    local removedCount = 0

    -- 3. 遍历种子，检查条件
    for i = 1, #candidates do
        local t = candidates[i]
        local hash = t["hash"]
        local name = t["name"]

        if not config.ShouldRemove(t) then
            goto continue_loop
        end

        -- 满足删除条件

        -- 3a. 备份（如果启用）
        if enableBackup then
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
                print(string.format("[BACKUP] 备份种子: %s", name))

                if config.PreBackup then
                    local newT = config.PreBackup(t)
                    if newT == nil then
                        print("[ERROR] PreBackup 失败，停止处理。请检查种子状态后手动处理。")
                        return 2
                    end
                    t = newT
                end

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

        -- 3b. 删除
        print(string.format("[DELETE] 删除种子: %s", name))
        local delOk
        if safeDelete then
            delOk = sd.SafeDelete(hash)
        else
            delOk = qb:DeleteTorrent(hash, true)
        end
        if not delOk then
            print("[ERROR] 删除失败: " .. tostring(_G.LastError))
            return 0
        end
        removedCount = removedCount + 1

        ::continue_loop::
    end

    print(string.format("[INFO] 完成，共删除 %d 个种子。", removedCount))
    return 1
end

return M
