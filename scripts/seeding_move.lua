--[[
    general_move.lua
    通用种子整理脚本库
    
    提供 Run(config) 函数，执行通用的“源盘检查 -> 筛选 -> 预处理 -> 备份 -> 目标盘检查(清理) -> 移动”流程。
]]

local M = {}

--[[
    config 结构:
    {
        SourceDrive      = "C:\\",             -- 源盘路径（用于检查空间）
        SourceMinSpace   = 100 * 1024^3,       -- 源盘最小保留空间 (bytes)
        GetCandidates    = function() -> {},   -- 返回待处理种子列表
        PreBackup        = function(t) -> {},  -- (可选) 预处理，如重命名。返回修改后的种子对象(或仅执行副作用)
        GetBackupPath    = function(t) -> "",  -- 返回备份目标路径 (文件夹)
        Overwrite        = nil,                -- (可选) 备份覆盖策略: true=覆盖, false=跳过, nil=报错(默认)
        TargetDrive      = "E:\\",             -- 目标盘路径（用于检查空间）
        TargetMinSpace   = 200 * 1024^3,       -- 目标盘最小保留空间 (bytes)
        GetDeletable     = function(all) -> {},-- 返回可删除种子列表(按删除优先级排序)
        GetTargetCategory= function(t) -> ""   -- 返回目标分类名称
    }
]]
function M.Run(config)
    print("========================================")
    print("  seeding_move.lua 开始执行")
    print("  Source: " .. (config.SourceDrive or "N/A"))
    print("  Target: " .. (config.TargetDrive or "N/A"))
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
        local size = t["size"]
        
        print(string.format("[INFO] 处理种子: %s (%s)", name, hash))
        
        -- 4. 预处理 (如重命名)
        if config.PreBackup then
            local newT = config.PreBackup(t)
            if newT == nil then
                -- PreBackup 失败（如重命名冲突），停止并等待人工处理
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
        
        -- 6. 检查目标盘空间并清理
        if config.TargetDrive and config.TargetMinSpace then
            while true do
                local free = qb:GetLocalFreeSpace(config.TargetDrive)
                if free == nil then
                    print("[ERROR] 无法获取目标盘空间")
                    return 0
                end
                
                local needed = config.TargetMinSpace + size
                if free >= needed then
                    break -- 空间足够
                end
                
                print(string.format("[INFO] 目标盘空间不足 (Free: %.2f GB, Need: %.2f GB), 开始清理...", 
                    free / 1024^3, needed / 1024^3))
                
                -- 获取全部种子用于筛选可删除项
                local all = qb:GetTorrents()
                if not all then return 0 end
                
                local deletables = config.GetDeletable(all)
                if not deletables or #deletables == 0 then
                    print("[WARN] 空间不足且无可删除种子！返回不再重试(2)。")
                    -- 或者返回 0 等待？视情况而定，这里返回 2 避免死循环备份
                    return 2
                end
                
                -- 逐个删除直到空间足够
                local freedSomething = false
                for i, d in ipairs(deletables) do
                    local dHash = d["hash"]
                    local dSize = d["size"]
                    
                    -- 计算文件数用于等待
                    local files = qb:GetFiles(dHash)
                    local fileCount = files and #files or 0
                    local waitTime = math.max(1, math.ceil(fileCount / 20))
                    
                    print(string.format("[DELETE] 删除种子: %s (Size: %.2f GB), 等待 %d 秒...", 
                        d["name"], dSize / 1024^3, waitTime))
                        
                    local ok = qb:DeleteTorrent(dHash, true) -- true = delete files
                    if ok then
                        qb:Sleep(waitTime)
                        freedSomething = true
                        
                        -- 再次检查空间
                        local newFree = qb:GetLocalFreeSpace(config.TargetDrive)
                        if newFree and newFree >= needed then
                            break -- 空间已够，跳出删除循环
                        end
                    else
                        print("[ERROR] 删除失败: " .. tostring(_G.LastError))
                    end
                end
                
                if not freedSomething then
                    print("[ERROR] 未能成功删除任何种子，空间依然不足。")
                    return 2
                end
                -- 循环回到 while true 继续检查空间
            end
        end
        
        -- 7. 移动分类
        if config.GetTargetCategory then
            local newCat = config.GetTargetCategory(t)
            if newCat and newCat ~= t["category"] then
                print(string.format("[INFO] 移动到分类: %s", newCat))
                local ok = qb:ChangeCategory(hash, newCat)
                if not ok then
                    print("[ERROR] 移动分类失败: " .. tostring(_G.LastError))
                    return 0
                end
                
                -- 等待移动完成
                print("[INFO] 等待移动数据完成...")
                while true do
                    qb:Sleep(2)
                    local polls = qb:GetTorrents(hash) -- 使用特定 Hash 查询
                    if not polls or #polls == 0 then
                        print("[WARN] 种子在移动中丢失？")
                        break
                    end
                    
                    local current = polls[1]
                    if current["state"] ~= "moving" then
                        print(string.format("[INFO] 移动完成，状态: %s", current["state"]))
                        break
                    end
                end
            end
        end
        
        -- 循环继续处理下一个
    end
end

return M
