--[[
    leech_move.lua
    功能：Leech 种子处理 (Finished -> Backup -> Delete)
    逻辑：
    1. 检查 D 盘空间 (保留 50G, 不足则等待)
    2. 筛选 Leech 分类 + uploading/stalledUP
    3. 备份至 I:\115\PT\Leech\{Category}\{Name}
    4. 备份成功后直接删除源任务和数据
]]

local M = {}

function M.Run()
    print("========================================")
    print("  leech_move.lua 开始执行")
    print("  Source: D:\\")
    print("  Target: I:\\115\\PT\\Leech\\...")
    print("========================================")

    local SourceDrive = "D:\\"
    local SourceMinSpace = 50 * 1024^3
    
    while true do
        -- 1. 检查源盘空间
        while true do
            local free = qb:GetLocalFreeSpace(SourceDrive)
            if free == nil then
                print("[ERROR] 无法获取源盘空间: " .. tostring(_G.LastError))
                return 0
            end
            
            if free >= SourceMinSpace then
                break
            end
            
            print(string.format("[WAIT] 源盘空间不足 (%.2f GB < %.2f GB)，等待 5 秒...", 
                free / (1024^3), SourceMinSpace / (1024^3)))
            qb:Sleep(5)
        end
        
        -- 2. 获取候选列表
        local all = qb:GetTorrents()
        if all == nil then
            print("[ERROR] 获取种子列表失败")
            return 0
        end
        
        local candidates = {}
        for i = 1, #all do
            local t = all[i]
            local cat = t["category"] or ""
            local state = t["state"] or ""
            
            if (cat == "Leech") and (state == "uploading" or state == "stalledUP") then
                table.insert(candidates, t)
            end
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
        
        -- 4. 备份
        local cat = t["category"] or "Unknown"
        local destDir = "I:\\115\\PT\\Leech\\" .. cat .. "\\" .. name
        print(string.format("[INFO] 备份到: %s", destDir))
        
        local success = qb:CopyTorrentFiles(hash, destDir)
        if not success then
            print("[ERROR] 备份失败: " .. tostring(_G.LastError))
            return 0 -- 重试
        end
        
        -- 5. 删除源
        print(string.format("[DELETE] 备份完成，删除源种子: %s", name))
        local ok = qb:DeleteTorrent(hash, true) -- true = delete data
        if not ok then
            print("[ERROR] 删除源种子失败: " .. tostring(_G.LastError))
            return 0
        end
        
        -- 循环继续处理下一个
    end
end

-- 如果作为模块被 require，返回 table；如果直接执行，可能需要调用 Run
-- 目前框架似乎是通过 require 然后调用 Run，或者直接作为脚本执行？
-- 假设 Program.cs 是通过 DoString 执行文件内容，或者通过 require 获取模块。
-- 根据之前的 script_demo.lua 和 agsvpt_move.lua，似乎是返回一个模块或直接执行。
-- agsvpt_move.lua 实际上是 require 了 general_move 然后调用 Run 返回结果。
-- 这里我们为了保持一致性，也返回一个模块或者直接执行。
-- 鉴于 agsvpt_move 是直接调用 gm.Run(...) 并返回结果。
-- 我们这里把 Run 暴露出来，或者直接运行。
-- 为了简单，直接运行 Run 并返回结果。

return M.Run()
