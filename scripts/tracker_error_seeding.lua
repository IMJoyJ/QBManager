--[[
    tracker_error_seeding.lua
    功能：Tracker 错误做种模块 (Module)
    
    基于 remove_by_condition，当种子所有的 Tracker 都处于错误状态时删除。
    注意：只要有一个 Tracker 正常工作，就不应删除。
    
    config = {
        GetCandidates    = function() -> {},     -- 返回所有相关种子
        EnableBackup     = false,                -- (可选) 默认 false
        TableName        = "xxx_backup",         -- 仅 EnableBackup 时需要
        PreBackup        = function(t) -> t,     -- (可选)
        GetBackupPath    = function(t) -> "",    -- 仅 EnableBackup 时需要
        Overwrite        = nil,                  -- (可选) 备份覆盖策略: true=覆盖, false=跳过, nil=报错(默认)
    }
]]

local rbc = require("scripts.remove_by_condition")

local M = {}

-- 定义 Tracker 状态码 (参考 qBittorrent 文档/源码)
-- 0: Tracker is disabled (default)
-- 1: Tracker has not been contacted yet
-- 2: Tracker has been contacted and is working
-- 3: Tracker is updating
-- 4: Tracker has been contacted, but it is not working (or access denied)
local TRACKER_STATUS_WORKING = 2

function M.Run(config)
    print("========================================")
    print("  tracker_error_seeding.lua 开始执行")
    print("========================================")

    return rbc.Run({
        TableName     = config.TableName,
        GetCandidates = config.GetCandidates,
        EnableBackup  = config.EnableBackup,
        SafeDelete    = config.SafeDelete,
        Overwrite     = config.Overwrite,
        PreBackup     = config.PreBackup,
        GetBackupPath = config.GetBackupPath,
        ShouldRemove  = function(t)
            local hash = t["hash"]
            local trackers = qb:GetTrackers(hash)
            
            if not trackers or #trackers == 0 then
                -- 没有 Tracker 的种子（如 DHT 种子），保留
                return false
            end

            -- 检查是否所有 Tracker 都出错
            -- 策略：只要有一个 Tracker 处于 "非错误" 状态，就保留
            -- 状态码：
            -- 0: Disabled (Error-like)
            -- 1: Not contacted (Wait) -> Keep
            -- 2: Working (OK) -> Keep
            -- 3: Updating (Wait) -> Keep
            -- 4: Not working (Error) -> Ignore (unless all are error)

            for i = 1, #trackers do
                local status = trackers[i]["status"]
                if status == 1 or status == 2 or status == 3 then
                    -- 发现 正常/等待/更新中 的 Tracker，保留种子
                    return false
                end
            end

            -- 代码运行到这里，说明所有 Tracker 都在报错 (status 4) 或禁用
            print(string.format("[TrackerError] 种子 %s 所有 Tracker 异常 (Count: %d)，准备删除。", t["name"], #trackers))
            for i = 1, #trackers do
                local tr = trackers[i]
                print(string.format("  - %s: Status %d, Msg: %s", tr.url, tr.status, tr.msg))
            end
            return true
        end,
    })
end

return M
