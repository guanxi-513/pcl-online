# CPT 方舟领航官网

项目展示名称：**CPT 方舟领航**。功能副标题：**Minecraft 启动与服务器管理工具**。

官网、统计后台、更新接口、邮箱账号与联机中继属于同一个服务。需要 Node.js 24 或更高版本，运行前执行 `npm ci` 安装 Nodemailer、ws 和 minecraft-protocol。

邮箱配置、账号 API 和联机流程见 `docs/rooms-and-accounts.md`。后台新增“用户与房间”“邮箱配置”，未配置 SMTP 时不能邮箱注册。

## 本地启动

临时预览可运行 `node Website/preview.mjs`，控制台会显示随机后台密码。预览数据只保存在内存中，退出后消失。

```powershell
$env:ADMIN_PASSWORD = '替换为至少16位的随机管理密码'
$env:PUBLIC_ORIGIN = 'http://127.0.0.1:3210'
node Website/server.mjs
```

- 官网：`http://127.0.0.1:3210/`
- 后台：`http://127.0.0.1:3210/admin`
- 数据库：`Website/data/community.sqlite`，请定期备份，勿提交到源码仓库。
- 测试：在 `Website` 目录运行 `npm test`。

## 正式部署

Linux Docker 部署脚本：上传整个 `Website` 目录到服务器的独立目录，先将官网域名及 `*.cpts.pw` 解析到服务器，再以 root 执行 `bash deploy.sh 你的官网域名`。脚本使用隔离的 Node.js 24 容器、独立持久化数据库和 Caddy HTTPS，不修改已有网站；80/443/25565 已被占用或已有 CraftPilot 容器时拒绝覆盖。管理密码自动生成，保存在仅 root 可读的 `.env` 中。Node HTTP 端口不暴露公网，仅新增 Minecraft TCP 25565。

当前已部署到 `https://craftpilot.tolike.cn`，后台为 `/admin`，域名指向 `103.116.247.92`。已启用 `前缀.cpts.pw` Minecraft Java 游戏网关；目标游戏仍由房主 CPT 客户端连接，不在网站服务器安装 Minecraft。维护与证书记录见 `docs/website-deployment.md`。

1. 把源码部署到网站服务器，安装 Node.js 24。
2. 通过服务管理器设置 `ADMIN_PASSWORD`、`PUBLIC_ORIGIN=https://你的域名`、`HOST=127.0.0.1` 和 `PORT=3210`。
3. 使用 HTTPS 反向代理转发到 `127.0.0.1:3210`；不要直接开放 Node 端口。
4. 反向代理应对 `/api/admin/login` 和 `/api/telemetry/heartbeat` 增加来源限流。应用不信任任意 `X-Forwarded-For`；经过代理后应用限流按代理地址计算。
5. 关闭统计接口的访问日志或明确配置其保存期限，避免反向代理额外记录用户 IP。
6. 启动器已连接 `https://craftpilot.tolike.cn`。更换官网域名时同步修改 `Modules/Base/ModBase.vb` 中的 `ProductWebsite`，并检查关于页和首次启动说明的链接。
7. 非 Docker 部署游戏网关时额外设置 `MC_GATEWAY_ENABLED=true`、`MC_GATEWAY_PORT=25565`，开放 TCP 25565 并将 `*.cpts.pw` 解析到本机。不要用 HTTP 反向代理转发此端口。

Windows 下可运行 `node test-client.mjs` 验证实际 .NET 房间客户端，运行 `node test-game-client.mjs` 验证 .NET 房主、Minecraft 状态/登录及加密数据转发；需要先构建 `PCLCS`。后者仅使用隔离内存账号，不连接生产数据库。

## 发布更新

后台填写版本号、内部版本号、通道、更新说明、迅雷网盘分享地址和提取码，然后发布。

- 正式版当前源码内部版本号为 `408`，快照版为 `410`。首次测试发布应使用更大的值。
- 发布的安装包也必须同步修改 `Modules/Base/ModBase.vb` 中的显示版本和对应通道内部版本号，否则升级后仍会收到旧提示。
- 每个通道的内部版本号必须递增。记录可撤回或恢复；修改链接时发布更高内部版本号的新记录。
- 只接受 `https://pan.xunlei.com/...` 分享链接，不接受安装包直链或其他网盘。
- 启动器只提示并打开迅雷分享页面，不自动下载、运行或替换文件。
- 官网使用真实发布信息显示版本与说明，没有发布记录时显示空状态。

## 统计与隐私

统计默认为关闭，由用户在启动器中主动启用。启动器全局“匿名数据收集”开关也会阻止上报。

- 随机 UUID 代表安装标识，不使用硬件指纹。
- 每 15 分钟发送安装标识、版本号、通道，不包含账号、SSH 密码、主机或服务器文件。
- 安装标识保存于本地注册表；重复心跳不会增加安装量。
- 后台首页改为累计注册用户、24 小时和 30 天活跃用户、账号状态，以及最近 30 天每日新增注册/活跃用户。统计按 CPT 账号 ID 去重，不按设备或登录会话数量计算。
- 登录和有效鉴权请求（含房间心跳）记录账号活动；未登录启动器不算活跃用户。用户日活明细保留 90 天。原安装表与匿名心跳保留兼容，但不混入用户统计。
- 升级时仅用已有账号最近登录/会话活动时间迁移一次可证实的活动日期，不把设备历史当作用户历史、不推断缺失日活。
- 日期按 UTC 分桶。日活明细保留 90 天，365 天未活跃的安装记录自动清理，因此“累计安装量”指数据库保留期内的去重安装量。
- 关闭统计后停止后续请求，不会删除已提交的匿名记录。已在途的请求可能完成。
- 上报接口不要求登录，统计可能被模拟请求影响，不应作为计费或真实人数凭证。
- 管理密码来自环境变量；会话为 HttpOnly/SameSite Cookie，正式 HTTPS 部署额外使用 Secure 属性，并对写操作验证 Origin 和 CSRF。

这是 HUASQ 维护的第三方基于 PCL 独立二次创作版本。CPT 增补内容 © 2026 HUASQ；PCL 原始代码及第三方组件版权仍归各自权利人。发布修改后的软件时请遵守根目录 `LICENCE`，公开相应源码并保留原作者署名和赞助链接。原指南要求重度使用的衍生软件名称以 Plain Craft Launcher (PCL) 开头；当前 CPT 方舟领航名称不符合该项要求，正式分发前需取得原作者的相应许可或调整分发名称。
