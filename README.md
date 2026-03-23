# QBManager

QBManager 是一款基于 .NET 10 构建的 qBittorrent 自动化管理工具。它通过连接 qBittorrent 的 WebUI 端点，配合强健的 Lua 5.4 脚本引擎，实现高度定制化的全自动种子管理。

它不仅能提供诸如清理失效种子、基于做种率或时间自动删种的基础功能，更支持复杂的条件检索、分盘监控、状态机流转操作以及安全删种（防止交叉做种文件误删）。

## 主要特色

- **纯 Lua 驱动策略**：所有的监控、移动、整理、删除动作都由轻量级但功能完整的 Lua 脚本配置，可根据不同的 PT/BT 站点或分类建立专用规则。
- **模块化代码复用**：内置了一套完善的逻辑流转模式。你可以通过 `require()` 灵活组合诸如 `seeding_move` (自动转移做种文件)、`leech_move` (完结归档搬运) 和 `safe_delete` (防止删错关联种子的保护查重锁) 等高度可复用的控制组件。
- **深度防灾恢复**：实现了针对错误分类转移、Tracker 连接失败惩罚、甚至底层 `content_path` 和 `save_path` 紊乱归一化的专属恢复机制。
- **C# 宿主性能**：宿主程序接管了繁重的文件 IO 迁移、跨盘检查和 HTTP(S) 请求拦截，通过将包装好的安全方法出让给脚本侧，兼顾了 C# 的强类型保障与 Lua 的调度灵活性。配合内置的 SQLite 支持持久化状态。

## 快速开始

### 统一部署
利用 .NET 的发布指令，直接输出清爽干净的单执行文件（内置配置文件及所有依赖项复制规则）：
```bash
dotnet publish -c Release -r win-x64
```
随后前往 `bin/Release/net10.0/win-x64/publish/` 目录下即可获得打包好的整套运行生态。

### 配置连接
修改根目录（或发布目录）下的 `config.json`，配置连接到你的 qBittorrent 客户端：

```json
{
  "server": {
    "base_url": "http://127.0.0.1:8080",
    "username": "admin",
    "password": "your_password"
  },
  "settings": {
    "script_interval_seconds": 5,
    "loop_interval_seconds": 300,
    "max_retry_attempts": 3
  },
  "database_path": "data.db",
  "scripts": [
    "scripts/script1.lua",
    "scripts/script2.lua",
    "scripts/script3.lua"
  ]
}
```

配置说明：
- `scripts` 数组决定了每次大轮询中哪些 Lua 脚本将被按顺序加载和执行。
- `script_interval_seconds` 是每个独立脚本执行完毕后的间隙冷却。
- `loop_interval_seconds` 是所有脚本跑完一轮之后，进入下一轮整体休眠等待的总时长。

## API 文档

宿主层面为 Lua 环境暴露了安全且结构严谨的面向对象 API 和属性集（全局挂载为 `qb` 以及针对捕捉异常的 `_G.LastError` 等）。
具体接口查阅见：
- [LUA_API.zhCN.md](LUA_API.zhCN.md) - 中文手册
- [LUA_API.md](LUA_API.md) - 英文手册
