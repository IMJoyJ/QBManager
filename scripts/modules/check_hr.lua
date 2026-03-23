--[[
    check_hr.lua
    HR（Hit-and-Run）规则检查模块

    规则：在 X 天内必须做种满 Y 小时，或分享率超过 Z，才允许删除。

    使用方法：
        local hr = require("scripts.modules.check_hr")
        local ok = hr.IsHRRuleSatisfied(t, x_days, y_hours, z_ratio)

    参数说明：
        t        - GetTorrents() 返回的种子对象
        x_days   - 宽限天数（距添加时间）
        y_hours  - 最低做种小时数
        z_ratio  - 最低分享率

    返回值：
        true   - 满足 HR 规则，允许删除
        false  - 尚未满足，禁止删除

    ─────────────────────────────────────────────
    实现细节（"等待 Tracker 汇报"的代理指标）
    ─────────────────────────────────────────────
    qBittorrent Web API 的 trackers 端点不直接暴露"最后汇报时间"字段。
    本模块利用种子的 reannounce 字段（距下次 Tracker 汇报的剩余秒数）推算
    "预期下次汇报绝对时间" = os.time() + reannounce。

    原理：两次检查之间若无新汇报，reannounce 与 os.time() 的变化量等量抵消，
    推算出的绝对时间近似不变；一旦发生汇报，reannounce 重置为完整 interval
    （例如 1800 秒），导致新推算值大幅跳增。
    差值 > 3 秒（防抖）即认定发生了至少一次新汇报。

    流程：
    - 首次满足 XYZ → 记录 (os.time() + reannounce) 到 SQLite → 返回 false
    - 再次检查同一种子 → 若新推算值 - 存储值 > 3，认为已汇报 → 返回 true

    ─────────────────────────────────────────────
    SQLite 表：hr_condition_met
    ─────────────────────────────────────────────
    hash          TEXT PRIMARY KEY
    satisfied_at  INTEGER  -- 首次满足条件的 Unix 时间戳
    next_announce INTEGER  -- 存储满足条件时的 (os.time() + reannounce) 估算值
]]

local M = {}

local TABLE_NAME  = "hr_condition_met"
local CLEANUP_GAP = 3600  -- 每小时最多执行一次清理
local ANNOUNCE_DEBOUNCE = 3  -- 防抖阈值：差值 <= 3 秒视为未发生新汇报

-- 模块级变量：上次清理时间（进程级别，重启后重置）
local _lastCleanupTime = 0

-- ─────────────────────────────────────────────
--  内部：初始化表
-- ─────────────────────────────────────────────
local function _ensureTable()
    local sql = string.format([[
        CREATE TABLE IF NOT EXISTS %s (
            hash          TEXT PRIMARY KEY,
            satisfied_at  INTEGER NOT NULL,
            next_announce INTEGER NOT NULL
        )
    ]], TABLE_NAME)
    local ok = qb:DbExecute(sql)
    if ok == nil then
        print("[check_hr] [ERROR] 创建表失败: " .. tostring(_G.LastError))
        return false
    end
    return true
end

-- ─────────────────────────────────────────────
--  内部：清理已失效种子（超过1小时才执行一次）
-- ─────────────────────────────────────────────
local function _maybeCleanup()
    local now = os.time()
    if now - _lastCleanupTime < CLEANUP_GAP then return end
    _lastCleanupTime = now

    -- 获取当前所有种子的 hash 集合
    local all = qb:GetTorrents()
    if not all then
        print("[check_hr] [WARN] 清理时无法获取种子列表，跳过")
        return
    end

    local existing = {}
    for i = 1, #all do
        existing[all[i]["hash"]] = true
    end

    -- 查出 SQLite 中所有记录
    local rows = qb:DbQuery(string.format("SELECT hash FROM %s", TABLE_NAME))
    if not rows then return end

    local deleted = 0
    for i = 1, #rows do
        local h = rows[i]["hash"]
        if not existing[h] then
            qb:DbExecute(string.format(
                "DELETE FROM %s WHERE hash = @p1", TABLE_NAME), h)
            deleted = deleted + 1
        end
    end

    if deleted > 0 then
        print(string.format("[check_hr] [INFO] 清理了 %d 条已失效的 HR 记录", deleted))
    end
end

-- ─────────────────────────────────────────────
--  公开函数
-- ─────────────────────────────────────────────

---判断种子是否已满足 HR 规则，允许删除。
---@param t          table   GetTorrents() 返回的种子对象
---@param x_days     number  宽限天数（距添加时间）
---@param y_hours    number  最低做种小时数
---@param z_ratio    number  分享率替代条件（超过此值也视为满足）
---@return boolean
function M.IsHRRuleSatisfied(t, x_days, y_hours, z_ratio)
    if not _ensureTable() then return false end

    local hash        = t["hash"]
    local seedingTime = t["seeding_time"] or 0   -- 做种秒数
    local ratio       = t["ratio"] or 0.0        -- 分享率
    local reannounce  = t["reannounce"]           -- 距下次汇报剩余秒数

    local now    = os.time()
    local y_secs = y_hours * 3600

    -- ── 1. 判断是否满足 XYZ 条件 ──────────────────────────────────────
    local xyzOk = (ratio >= z_ratio) or (seedingTime >= y_secs)

    if not xyzOk then
        _maybeCleanup()
        return false
    end

    -- ── 2. XYZ 条件满足，推算预期下次汇报绝对时间 ─────────────────────
    -- reannounce 无效（nil 或负数）时用 -1 标记
    local curNextAnnounce
    if type(reannounce) == "number" and reannounce >= 0 then
        curNextAnnounce = now + reannounce
    else
        curNextAnnounce = -1
    end

    -- ── 3. 查询 SQLite ────────────────────────────────────────────────
    local row = qb:DbQuery(string.format(
        "SELECT satisfied_at, next_announce FROM %s WHERE hash = @p1", TABLE_NAME),
        hash)

    if not row or #row == 0 then
        -- 首次满足：写入记录，本次返回 false（等待 Tracker 重新汇报后确认）
        qb:DbExecute(string.format(
            "INSERT OR REPLACE INTO %s (hash, satisfied_at, next_announce) VALUES (@p1, @p2, @p3)",
            TABLE_NAME), hash, now, curNextAnnounce)
        print(string.format(
            "[check_hr] [INFO] 种子首次满足 HR 条件，等待 Tracker 汇报: %s (next_announce=%d)",
            hash, curNextAnnounce))
        _maybeCleanup()
        return false
    end

    -- ── 4. 已有记录：检测 next_announce 是否大幅跳增（新汇报发生）─────
    local storedNextAnnounce = row[1]["next_announce"]

    -- 两个值均有效，且新推算值比存储值大超过防抖阈值 → 发生了新汇报
    if curNextAnnounce ~= -1 and storedNextAnnounce ~= -1
       and (curNextAnnounce - storedNextAnnounce) > ANNOUNCE_DEBOUNCE then
        print(string.format(
            "[check_hr] [INFO] 种子已完成 Tracker 汇报，满足 HR 规则: %s (next_announce: %d -> %d, delta=%d)",
            hash, storedNextAnnounce, curNextAnnounce,
            curNextAnnounce - storedNextAnnounce))
        -- 注意：此处不删除 SQLite 记录，因为上层可能因为其他条件未实际删除该种子
        _maybeCleanup()
        return true
    end

    -- 推算时间无显著变化（或值无效），尚未确认新汇报
    print(string.format(
        "[check_hr] [INFO] 等待 Tracker 汇报中: %s (stored=%d, current=%d)",
        hash, storedNextAnnounce, curNextAnnounce))
    _maybeCleanup()
    return false
end

return M
