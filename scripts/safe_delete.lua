--[[
    safe_delete.lua
    功能：安全删除种子模块 (Utility)
    
    删除种子时检测文件重叠——
    如果被删种子的文件仍被其他种子引用，保留这些文件，
    仅删除不再被任何种子使用的文件。
    
    典型场景：PT 站种子 A (a,b,c) 被更新版种子 B (a,b,c,d) 取代，
    管理员下架 A 后 A 报错，但删除 A 不应损坏 B 的文件。
    
    提供:
      SafeDelete(hash)       -- 安全删除单个种子
      SafeDeleteAll(hashes)  -- 安全删除多个种子（批量，共享文件检测更准确）
]]

local M = {}

-- 路径分隔符统一为 \（Windows）
local SEP = "\\"

--- 获取文件路径的父目录
local function parentDir(path)
    -- 找最后一个 \ 或 /
    local i = path:match(".*()[\\/]")
    if i and i > 1 then
        return path:sub(1, i - 1)
    end
    return nil
end

--- 统一路径分隔符为 \，并去除末尾分隔符
local function normalizePath(p)
    if not p then return nil end
    p = p:gsub("/", SEP)
    -- 去除末尾 \（但保留盘符根目录如 D:\）
    if #p > 3 and p:sub(-1) == SEP then
        p = p:sub(1, -2)
    end
    return p
end

--- 拼接 save_path 和文件相对路径
local function fullFilePath(savePath, relName)
    savePath = normalizePath(savePath)
    relName = normalizePath(relName)
    if savePath:sub(-1) == SEP then
        return savePath .. relName
    end
    return savePath .. SEP .. relName
end

--- 收集目录并向上清理空目录，直到 stopAt
local function cleanEmptyDirs(dirSet, stopAt)
    stopAt = normalizePath(stopAt)
    -- 将 dirSet 按路径长度降序排列（从最深开始）
    local dirs = {}
    for d, _ in pairs(dirSet) do
        dirs[#dirs + 1] = d
    end
    table.sort(dirs, function(a, b) return #a > #b end)

    for _, dir in ipairs(dirs) do
        local current = dir
        while current and #current > #stopAt do
            local result = qb:DeleteLocalDir(current)
            if result == true then
                -- 删除成功，继续向上
                print(string.format("[SafeDelete] 清理空目录: %s", current))
                current = parentDir(current)
            else
                -- 目录非空或出错，停止向上
                break
            end
        end
    end
end

--[[
    安全删除单个种子。
    1. 获取目标种子的文件列表和 save_path
    2. 查找同 save_path 的其他种子，收集其文件集合
    3. 仅删除不被其他种子引用的文件
    4. 清理空目录
    
    返回: true 成功, nil 失败
]]
function M.SafeDelete(hash)
    -- 获取所有种子列表（用于查找 save_path 和共享种子）
    local allTorrents = qb:GetTorrents()
    if not allTorrents then
        print("[SafeDelete] 获取种子列表失败: " .. tostring(_G.LastError))
        return nil
    end

    -- 找到目标种子的 save_path
    local targetSavePath = nil
    for i = 1, #allTorrents do
        if allTorrents[i]["hash"] == hash then
            targetSavePath = normalizePath(allTorrents[i]["save_path"])
            break
        end
    end

    if not targetSavePath then
        print("[SafeDelete] 目标种子不存在，可能已被删除。")
        return true
    end

    -- 获取目标种子的文件列表
    local targetFiles = qb:GetTorrentFiles(hash)
    if not targetFiles or #targetFiles == 0 then
        -- 没有文件，直接删除种子条目
        print("[SafeDelete] 种子无文件，直接删除条目。")
        return qb:DeleteTorrent(hash, false)
    end

    -- 收集同 save_path 的其他种子的文件集合
    local inUseFiles = {} -- { normalizedRelPath = true }
    for i = 1, #allTorrents do
        local t = allTorrents[i]
        if t["hash"] ~= hash then
            local otherSavePath = normalizePath(t["save_path"])
            if otherSavePath == targetSavePath then
                local otherFiles = qb:GetTorrentFiles(t["hash"])
                if otherFiles then
                    for j = 1, #otherFiles do
                        local relName = normalizePath(otherFiles[j]["name"])
                        if relName then
                            inUseFiles[relName] = true
                        end
                    end
                end
            end
        end
    end

    -- 计算需要删除的文件 vs 需要保留的文件
    local filesToDelete = {}
    local filesKept = 0
    for i = 1, #targetFiles do
        local relName = normalizePath(targetFiles[i]["name"])
        if relName then
            if inUseFiles[relName] then
                filesKept = filesKept + 1
                print(string.format("[SafeDelete] 保留共享文件: %s", relName))
            else
                filesToDelete[#filesToDelete + 1] = relName
            end
        end
    end

    print(string.format("[SafeDelete] 文件统计: 总计 %d, 删除 %d, 保留(共享) %d",
        #targetFiles, #filesToDelete, filesKept))

    -- 先从 qBittorrent 移除种子条目（不删文件）
    local removeOk = qb:DeleteTorrent(hash, false)
    if not removeOk then
        print("[SafeDelete] 移除种子条目失败: " .. tostring(_G.LastError))
        return nil
    end

    -- 删除非共享文件
    local parentDirs = {}
    for _, relName in ipairs(filesToDelete) do
        local fullPath = fullFilePath(targetSavePath, relName)
        print(string.format("[SafeDelete] 删除文件: %s", fullPath))
        local delOk = qb:DeleteLocalFile(fullPath)
        if not delOk then
            print("[SafeDelete] [WARN] 删除文件失败: " .. tostring(_G.LastError))
            -- 继续删除其他文件，不中断
        end
        -- 收集父目录用于后续清理
        local pDir = parentDir(fullPath)
        if pDir then
            parentDirs[pDir] = true
        end
    end

    -- 清理空目录
    if next(parentDirs) then
        cleanEmptyDirs(parentDirs, targetSavePath)
    end

    print("[SafeDelete] ✓ 安全删除完成。")
    return true
end

return M
