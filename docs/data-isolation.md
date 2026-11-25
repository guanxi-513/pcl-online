# CPT 数据隔离

CPT 的生成文件使用独立目录与标识，避免修改同目录或共同游戏目录里的 PCL 配置。

| 数据 | CPT 位置 |
| --- | --- |
| 启动器设置 | 程序旁 `CPT/Setup.ini` |
| 加密账号 | 程序旁 `CPT/accounts.dat` |
| 日志、启动脚本 | `CPT/Log1~5.txt`、`CPT/LatestLaunch.bat` |
| 自定义主页、背景、音乐 | `CPT/Custom.xaml`、`CPT/Pictures/`、`CPT/Musics/` |
| 用户级数据 | `%AppData%/CPT/` |
| 游戏目录索引与选择 | 游戏目录的 `CPT.ini` |
| 版本独立设置与图标 | 版本目录的 `CPT/Setup.ini`、`CPT/config.json`、`CPT/Logo.png` |
| 临时版本标记 | `.cptignore` |
| 离线皮肤资源包 | `CPT Skin.zip` |
| 本地客户端整合包 | `CPT/ServerClients/` |
| 远端代理工作目录 | SSH 用户主目录的 `.cpt-server/` |
| 远端配置与待安装队列 | `.cpt-server.json`、`.cpt-pending.json`、`.cpt-pending/` |
| 远端客户端包、日志 | `.cpt-client.mrpack`、`.cpt-client.json`、`cpt-console.log` |

已有独立的 `HKCU/Software/CraftPilot` 注册表、`CraftPilot_SingletonMutex`、`%TEMP%/CraftPilot` 缓存及服务器根目录继续保留，避免丢失现有连接或与旧 CPT 同时运行。这些名称不是 PCL 的标识。

## 旧数据迁移

- 只有程序旁旧 `PCL/accounts.dat` 存在时，才识别为之前的 CPT 数据目录。复制缺失文件到 `CPT/`，不自动导入纯 PCL 的启动器设置。
- 共同游戏目录的 `PCL.ini`、版本目录的 `PCL/` 设置只在首次读取时复制快照，旧文件保留。修改、重置、清理仅写 CPT 路径。
- 迁移标记在清理版本设置后仍保留，避免旧设置重新导入；已有 CPT 文件不会被覆盖。
- 旧 CPT 远端 `.pcl-server.json` 元数据首次管理时复制为 `.cpt-*`，重写其中的管理路径，不删除原数据或修改游戏世界。
- DPAPI 密文不重新解密或更改用户归属，账号迁移仍限同一 Windows 用户使用。

Minecraft 标准文件，如 `server.properties`、`eula.txt`、`options.txt`、世界和模组目录，不能随产品改名。两个启动器仍不应同时安装、修改或运行同一游戏实例；配置隔离不等于游戏文件可以并发修改。
