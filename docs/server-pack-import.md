# 自定义开服包导入

在“我的服务器”暂无服务器时点击“添加服务器”进入部署，选择“导入自定义开服包”，然后选择 ZIP 开服包或单个服务端 JAR。导入只读取文件，不在本机执行启动脚本。

## 自动识别

- 单个 JAR：从 `version.json` 或 Mojang `META-INF/versions.list` 获取游戏版本；缺少版本时手动选择 Minecraft 版本。
- ZIP：自动去掉统一的外层目录，识别根目录 Java 启动行、服务端 JAR、Forge/NeoForge 参数文件、Fabric 属性及库路径。
- 读取 `server.properties` 的游戏端口、启动行或 `user_jvm_args.txt` 中的 `-Xms`/`-Xmx` 内存和常见 `-XX`/`-D` 参数。
- `variables.txt` 支持 `MINECRAFT_VERSION`、`MC_VERSION`、`MODLOADER`、`MODLOADER_VERSION` 和 `JAVA_VERSION`。
- 识别后显示包名、服务端类型、版本、内存、端口与启动入口。高级设置可覆盖最大内存、端口及 Java 版本。

远端自动补齐 Python 与匹配的 Java，不重新下载或替换包内服务端。包中的世界、模组、插件与配置保持原样；管理需要的 `enable-rcon`、私有 RCON 端口与随机密码会更新。游戏端口沿用识别值，EULA 需用户明确同意。

## 明确配置

无法唯一识别启动入口时，在 ZIP 根目录添加 `cpt-server.json`。该文件不是执行脚本：

```json
{
  "core": "Forge",
  "version": "1.21.1",
  "loaderVersion": "52.1.0",
  "javaMajor": 21,
  "minMemory": 1024,
  "memory": 4096,
  "port": 25565,
  "jvmArgs": ["-XX:+UseG1GC"],
  "launchArgs": ["@libraries/net/minecraftforge/forge/1.21.1-52.1.0/unix_args.txt", "nogui"]
}
```

普通 JAR 的启动参数使用 `["-jar", "server.jar", "nogui"]`。Forge/NeoForge 在远端根据系统选择同目录下的 `unix_args.txt` 或 `win_args.txt`，包内必须有目标系统对应文件。服务端类型支持 Vanilla、Paper、Fabric、Forge、NeoForge；加载器必须有明确版本。

复杂安装脚本、依赖 shell 变量或命令链的脚本、RAR/7z、不完整的下载式开服包不会自动执行。先在可信环境准备完整服务端包，或添加明确配置。

## 安全与限制

上传大小最多 2 GiB，ZIP 解压后最多 4 GiB、50,000 项。检查路径越界、Windows 保留文件名、大小写重复项、符号链接及 SHA-256；本地识别后包改变会要求重新导入。禁止自动导入 Java agent 参数。

部署仅使用新的空目录；代理在同级暂存目录完成后再提交，不覆盖已有服务器。管理生成的文件使用 `.cpt-*` 和 `cpt-console.log`，代理工作目录为 `.cpt-server/`；旧 CPT 元数据只读复制迁移。服务端和模组在远端运行时可以执行代码，因此必须信任开服包来源。未知模组的客户端要求仍需管理员核对，客户端同步不会发送服务器配置、世界或 SSH 凭据。
