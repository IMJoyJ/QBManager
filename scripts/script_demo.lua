--[[
    script_demo.lua
    QBManager 示例脚本
    
    演示：
    1. 获取种子列表并打印信息
    2. 获取磁盘剩余空间
    3. 错误处理协议 (nil → _G.LastError)

    返回值协议：
      1 = 成功 (Success)
      0 = 失败，需重试 (Failure, Retry)
      2 = 失败，无需重试 (Failure, No Retry)
]]

print("========================================")
print("  script_demo.lua 开始执行")
print("========================================")

-- 1. 获取所有种子
local torrents = qb:GetTorrents()
if torrents == nil then
    print("[ERROR] 获取种子列表失败: " .. tostring(_G.LastError))
    return 0  -- 返回 0, 宿主程序会重试
end

-- 2. 遍历种子列表, 打印信息
local count = 0
for i = 1, #torrents do
    local t = torrents[i]
    count = count + 1
    print(string.format("  [%d] %s", i, tostring(t["name"])))
    print(string.format("      Hash:     %s", tostring(t["hash"])))
    print(string.format("      State:    %s", tostring(t["state"])))
    print(string.format("      Size:     %s bytes", tostring(t["size"])))
    print(string.format("      Category: %s", tostring(t["category"])))
    print(string.format("      Path:     %s", tostring(t["content_path"])))
    print("")
end

print(string.format("共计 %d 个种子", count))

-- 3. 获取 C 盘剩余空间 (Windows 示例, Linux 可改为 "/")
local freeSpace = qb:GetLocalFreeSpace("C:\\")
if freeSpace == nil then
    print("[WARN] 获取磁盘剩余空间失败: " .. tostring(_G.LastError))
    -- 这不是致命错误, 继续执行
else
    local freeGB = freeSpace / (1024 * 1024 * 1024)
    print(string.format("C 盘剩余空间: %.2f GB", freeGB))
end

-- 4. 演示路径检查
local testPath = "C:\\Windows"
local exists = qb:PathExists(testPath)
if exists == nil then
    print("[WARN] PathExists 检查失败: " .. tostring(_G.LastError))
else
    print(string.format("路径 '%s' 是否存在: %s", testPath, tostring(exists)))
end

-- 5. 演示 URL 编码/解码
local encoded = qb:UrlEncode("hello world 你好")
if encoded ~= nil then
    print(string.format("UrlEncode: %s", tostring(encoded)))
    local decoded = qb:UrlDecode(tostring(encoded))
    if decoded ~= nil then
        print(string.format("UrlDecode: %s", tostring(decoded)))
    end
end

print("========================================")
print("  script_demo.lua 执行完毕")
print("========================================")

-- 返回 1 表示成功
return 1
