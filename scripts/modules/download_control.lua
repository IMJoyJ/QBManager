--[[
    download_control.lua
    功能：根据剩余空间阈值自动暂停或恢复下载。
    
    逻辑：
    - 获取所有的种子 (如果有配置 GetCandidates 则使用其返回的受控种子，否则默认获取所有种子)
    - 按 save_path 的驱动器/根目录分类
    - 对每个驱动器检查其剩余空间
    - 若剩余空间低于 MinFreeSpaceBytes，暂停该盘下所有处于下载状态（非做种）的任务
    - 若剩余空间恢复至 ResumeFreeSpaceBytes 以上，恢复该盘下所有处于暂停下载状态的任务
]]

local M = {}

function M.Run(config)
    print("========================================")
    print("  download_control.lua 开始执行")
    print("========================================")

    -- 默认暂定空间下限：10GB
    local minFreeSpaceBytes = config.MinFreeSpaceBytes or (10 * 1024 * 1024 * 1024)
    -- 默认恢复空间上限：15GB
    local resumeFreeSpaceBytes = config.ResumeFreeSpaceBytes or (15 * 1024 * 1024 * 1024)

    local torrents
    if type(config.GetCandidates) == "function" then
        torrents = config.GetCandidates()
    else
        torrents = qb:GetTorrents()
    end

    if not torrents then
        print("[ERROR] 获取受控种子列表失败")
        return 0
    end

    if #torrents == 0 then
        print("[INFO] 当前没有种子。")
        return 1
    end

    -- 下载系列状态，处于这些状态的会被考虑暂停
    local isDownloadingState = {
        ["downloading"] = true,
        ["metaDL"] = true,
        ["stalledDL"] = true,
        ["queuedDL"] = true,
        ["checkingDL"] = true,
        ["forcedDL"] = true,
        ["allocating"] = true
    }

    -- 暂停的下载状态，处于这个状态的会被考虑恢复
    local isPausedDLState = {
        ["pausedDL"] = true,
        ["error"] = true -- 如果因为磁盘满报错，也会在这里被恢复
    }

    -- 1. 按盘符/挂载点（根目录）对种子进行分组
    -- drive -> { active_downloads = {hash1, hash2}, paused_downloads = {hash3, hash4}, sample_path = "D:\\" }
    local driveGroups = {}

    for i = 1, #torrents do
        local t = torrents[i]
        local state = t["state"]
        local savePath = t["save_path"]
        local hash = t["hash"]

        -- 提取盘符或根目录
        -- Windows: "D:\" 或 "D:/xxx" -> "D:\"
        -- Linux: "/mnt/data/xxx" -> "/mnt/data" (qBittorrent save_path 难以绝对准确提取挂载点，我们使用 save_path 向上查找或暂时按顶层目录分组，或直接取 savePath 的盘符)
        
        local drive = ""
        -- 检测 Windows 盘符
        local driveMatch = savePath:match("^([a-zA-Z]:)")
        if driveMatch then
            drive = driveMatch .. package.config:sub(1,1)
        else
            -- Unix 风格，为了简单起见，取前两个目录级别，或者假设用户挂载在 /mnt/xxx, /volume1/xxx
            -- 我们直接用完整的 savePath 调用 GetLocalFreeSpace（GetLocalFreeSpace 会自动往上找挂载点）
            -- 为了合并相同的盘，我们需要一个代表。我们暂时用顶层目录代表，例如 "/mnt" 或 "/volume1"
            local rootMatch = savePath:match("^(/[a-zA-Z0-9_-]+/[a-zA-Z0-9_-]+)")
            if rootMatch then
                drive = rootMatch
            else
                local fallbackMatch = savePath:match("^(/[a-zA-Z0-9_-]+)")
                if fallbackMatch then drive = fallbackMatch else drive = "/" end
            end
        end

        if not driveGroups[drive] then
            driveGroups[drive] = {
                active_downloads = {},
                paused_downloads = {},
                sample_path = savePath -- 用来查询该组的剩余空间
            }
        end

        if isDownloadingState[state] then
            table.insert(driveGroups[drive].active_downloads, hash)
        elseif isPausedDLState[state] then
            table.insert(driveGroups[drive].paused_downloads, hash)
        end
    end

    -- 2. 检查每个盘符/挂载点，并执行对应操作
    for drive, group in pairs(driveGroups) do
        -- 仅当该盘下有下载相关的任务时才检查
        if #group.active_downloads > 0 or #group.paused_downloads > 0 then
            local freeSpace = qb:GetLocalFreeSpace(group.sample_path)
            
            if freeSpace == nil then
                print(string.format("[WARNING] 无法获取路径剩余空间: %s", group.sample_path))
            else
                local freeSpaceGB = freeSpace / (1024 * 1024 * 1024)
                print(string.format("[INFO] 驱动器/目录代表 [%s]: 剩余 %.2f GB", drive, freeSpaceGB))

                if freeSpace < minFreeSpaceBytes then
                    -- 剩余空间不足，暂停活动中的下载
                    if #group.active_downloads > 0 then
                        print(string.format("[ACTION] 空间低于 %.2f GB，暂停 %d 个下载任务...", 
                            minFreeSpaceBytes / (1024^3), #group.active_downloads))
                        
                        local hashesToPause = table.concat(group.active_downloads, "|")
                        local ok = qb:PauseTorrents(hashesToPause)
                        if ok then
                            print(string.format("[INFO] 成功暂停目录 [%s] 下的任务。", drive))
                        else
                            print("[ERROR] 暂停任务失败：" .. tostring(_G.LastError))
                        end
                    end
                elseif freeSpace >= resumeFreeSpaceBytes then
                    -- 剩余空间充足，恢复被暂停的下载
                    if #group.paused_downloads > 0 then
                        print(string.format("[ACTION] 空间达标 (>= %.2f GB)，准备恢复 %d 个下载任务...", 
                            resumeFreeSpaceBytes / (1024^3), #group.paused_downloads))

                        local hashesToResume = table.concat(group.paused_downloads, "|")
                        local ok = qb:ResumeTorrents(hashesToResume)
                        if ok then
                            print(string.format("[INFO] 成功恢复目录 [%s] 下的任务。", drive))
                        else
                            print("[ERROR] 恢复任务失败：" .. tostring(_G.LastError))
                        end
                    end
                end
            end
        end
    end

    print("========================================")
    print("  download_control.lua 执行完毕")
    print("========================================")
    
    return 1
end

return M
