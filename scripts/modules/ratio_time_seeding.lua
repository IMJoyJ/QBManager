--[[
    ratio_time_seeding.lua
    功能：分享率+时间做种模块 (Module)
    
    基于 remove_by_condition，当种子满足以下任一条件时删除：
    - 分享率 >= MaxRatio
    - 做种时长 >= MaxSeedingTime
    
    config = {
        MaxRatio         = 1.0,                  -- (可选) 最大分享率，默认 1.0
        MaxSeedingTime   = 7 * 86400,            -- (可选) 最大做种秒数，默认 7 天
        GetCandidates    = function() -> {},     -- 返回所有相关种子
        EnableBackup     = false,                -- (可选) 默认 false
        TableName        = "xxx_backup",         -- 仅 EnableBackup 时需要
        PreBackup        = function(t) -> t,     -- (可选)
        GetBackupPath    = function(t) -> "",    -- 仅 EnableBackup 时需要
        Overwrite        = nil,                  -- (可选) 备份覆盖策略: true=覆盖, false=跳过, nil=报错(默认)
    }
]]

local rbc = require("scripts.modules.remove_by_condition")

local M = {}

function M.Run(config)
    local maxRatio = config.MaxRatio or 1.0
    local maxTime = config.MaxSeedingTime or (7 * 86400)
    print(string.format("[ratio_time_seeding] MaxRatio: %.2f, MaxSeedingTime: %d s (%.1f days)",
        maxRatio, maxTime, maxTime / 86400))

    return rbc.Run({
        TableName     = config.TableName,
        GetCandidates = config.GetCandidates,
        EnableBackup  = config.EnableBackup,
        SafeDelete    = config.SafeDelete,
        Overwrite     = config.Overwrite,
        PreBackup     = config.PreBackup,
        GetBackupPath = config.GetBackupPath,
        ShouldRemove  = function(t)
            local ratio = t["ratio"] or 0
            local seedTime = t["seeding_time"] or 0
            return (ratio >= maxRatio) or (seedTime >= maxTime)
        end,
    })
end

return M
