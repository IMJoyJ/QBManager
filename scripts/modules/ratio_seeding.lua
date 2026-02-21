--[[
    ratio_seeding.lua
    功能：分享率做种模块 (Module)
    
    基于 remove_by_condition，当种子分享率超过设定值时删除。
    
    config = {
        MaxRatio         = 1.0,                  -- (可选) 最大分享率，默认 1.0
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
    print(string.format("[ratio_seeding] MaxRatio: %.2f", maxRatio))

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
            return ratio >= maxRatio
        end,
    })
end

return M
