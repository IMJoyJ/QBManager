--[[
    leech_move.lua
    功能：Leech 种子处理 (Finished -> Backup -> Delete) 用作通用模块
    
    提供 Run(config) 函数，执行通用的“源盘检查 -> 筛选 -> (预处理) -> 备份 -> 删除”流程。
]]

local M = {}

--[[
    config 结构:
    {
        SourceDrive      = "D:\\",             -- 源盘路径（用于检查空间）
        SourceMinSpace   = 50 * 1024^3,        -- 源盘最小保留空间 (bytes)
        GetCandidates    = function() -> {},   -- 返回待处理种子列表
        PreBackup        = function(t) -> {},  -- (可选) 预处理，如重命名。返回修改后的种子对象(或仅执行副作用)
        GetBackupPath    = function(t) -> "",  -- 返回备份目标路径 (文件夹)
        Overwrite        = nil,                -- (可选) 备份覆盖策略: true=覆盖, false=跳过, nil=报错(默认)
    }
]]
function M.Run(config)
    print("========================================")
    print("  leech_move.lua 开始执行")
    print("  Source: " .. (config.SourceDrive or "N/A"))
    print("========================================")

    while true do
        -- 1. 检查源盘空间
        if config.SourceDrive and config.SourceMinSpace then
            while true do
                local free = qb:GetLocalFreeSpace(config.SourceDrive)
                if free == nil then
                    print("[ERROR] 无法获取源盘空间: " .. tostring(_G.LastError))
                    return 0
                end
                
                if free >= config.SourceMinSpace then
                    break
                end
                
                print(string.format("[WAIT] 源盘空间不足 (%.2f GB < %.2f GB)，等待 5 秒...", 
                    free / (1024^3), config.SourceMinSpace / (1024^3)))
                qb:Sleep(5)
            end
        end
        
        -- 2. 获取候选列表
        local candidates = config.GetCandidates()
        if candidates == nil then
            print("[ERROR] 获取种子列表失败")
            return 0
        end
        
        if #candidates == 0 then
            print("[INFO] 没有符合条件的种子，任务完成。")
            return 1
        end
        
        -- 3. 处理第一个种子
        local t = candidates[1]
        local hash = t["hash"]
        local name = t["name"]
        
        print(string.format("[INFO] 处理种子: %s (%s)", name, hash))
        
        -- 4. 预处理 (如重命名)
        if config.PreBackup then
            local newT = config.PreBackup(t)
            if newT == nil then
                print("[ERROR] PreBackup 失败，停止处理。请检查种子状态后手动处理。")
                return 2
            end
            t = newT
        end
        
        -- 5. 备份
        if config.GetBackupPath then
            local destDir = config.GetBackupPath(t)
            if destDir then
                print(string.format("[INFO] 备份到: %s", destDir))
                local success = qb:CopyTorrentFiles(hash, destDir, config.Overwrite)
                if not success then
                    print("[ERROR] 备份失败: " .. tostring(_G.LastError))
                    return 0 -- 重试
                end
            end
        end
        
        -- 6. 删除源
        print(string.format("[DELETE] 备份完成，删除源种子: %s", name))
        local ok = qb:DeleteTorrent(hash, true) -- true = delete data
        if not ok then
            print("[ERROR] 删除源种子失败: " .. tostring(_G.LastError))
            return 0
        end
        
        -- 循环继续处理下一个
    end
end

return M
