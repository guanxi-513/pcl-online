# 官网与后台部署记录

部署日期：2026-10-03（Asia/Shanghai）。

- 官网：`https://craftpilot.tolike.cn/`
- 后台：`https://craftpilot.tolike.cn/admin`
- 域名 A 记录：`103.116.247.92`。
- 公开可信证书：Let's Encrypt，由 Caddy 自动签发和续期。
- 服务器：Ubuntu 18.04.6 LTS / x86_64，当前未部署 Minecraft。
- 文件目录：`/opt/craftpilot/site`
- 官网服务：Docker 容器 `craftpilot-site`，Node.js 24。
- HTTPS 入口：Docker 容器 `craftpilot-proxy`，Caddy 2。
- Node 服务端口没有映射到公网；仅 HTTP/HTTPS 入口发布 80/443。
- 2026-10-04 后续启用 Minecraft 游戏网关 TCP 25565；上述端口说明为首次部署状态，当前配置见下方“Minecraft 游戏域名”。
- 数据库卷：`craftpilot-data`，容器内 `/app/data/community.sqlite`。
- 管理密码：服务器 `/opt/craftpilot/site/.env`，权限 600。本记录不存储密码。
- 两个容器设置 `unless-stopped` 自动恢复，Docker 开机启动。
- 数据库备份：`/opt/craftpilot/site/backup.sh`，通过 cron 每日执行，保留 14 天，容器内 `/app/data/backups`。

## HTTPS 证书

正式入口已切换到 `craftpilot.tolike.cn`，使用 Let's Encrypt 公共证书，无需关闭证书验证或安装私有 CA。HTTP 域名和旧 HTTP IP 入口均跳转到域名 HTTPS。

切换域名前曾用 IP 私有 CA 验证服务，旧配置备份保留在服务器 `Caddyfile.ip-backup`，旧 CA 文件留在 `output/craftpilot-root.crt`。现在不需要安装该证书。

SHA-256 指纹：

```text
B5:32:47:89:A3:85:F0:66:92:C9:7C:C7:ED:5C:FE:73:97:61:57:2A:B0:B9:32:06:36:02:65:1A:29:B2:F4:E1
```

未自动安装旧根证书到用户系统，当前部署不使用它作为公开访问证书。

## 维护

```bash
docker ps
docker logs --tail 100 craftpilot-site
docker logs --tail 100 craftpilot-proxy
bash /opt/craftpilot/site/backup.sh
```

更新前先备份数据库。`.env`、数据库卷和 Caddy 数据卷必须保留，不运行删除卷的命令。

修改后台密码：更新 `.env` 中 `ADMIN_PASSWORD`，以原参数重新创建 `craftpilot-site` 容器，保留数据库卷。单纯 `docker restart` 不会重新载入修改后的环境文件。

## 验证

- 公网官网和后台页面返回 200，HTTP 返回 308 并跳转到 HTTPS。
- 域名证书在公开信任链下校验通过。后台登录、统计读取、发布记录读取、CSRF 拒绝和退出会话均验证。
- 浏览器登录和后台页面检查通过。
- 重启网站容器后仍返回 200，数据库文件持久化正常。
- 没有创建测试用户统计或虚假更新记录，当前安装统计和更新记录为空。

## CPT 品牌同步

2026-10-03 已同步官网、后台标题与 CPT 图标，维护者标为 HUASQ，保留原作者赞助与 PCL 原始版权，新增版权仅声明 CPT 增补内容。

- 更新前已执行数据库备份，并保留网页备份 `/opt/craftpilot/site/backups/cpt-branding-20261003-214507`。
- 保留旧镜像 `craftpilot-site:pre-cpt-branding` 和停止的旧容器 `craftpilot-site-pre-cpt-branding-20261003`，可用于回退。
- 新容器继续使用原 `.env`、`craftpilot-data` 卷、`craftpilot` 网络及 `unless-stopped` 策略；未修改密码、数据库、证书入口或其他服务。
- 官网与后台 HTTPS 返回 200，数据库文件仍在，未发布测试更新或生成安装统计。

Ubuntu 18.04 已是老旧系统；容器隔离不能替代宿主机安全更新。正式长期运行前应规划升级至受支持的 Ubuntu LTS，并更换已在聊天中提供的 root 密码。

## 邮箱与联机接口

2026-10-04 新增 CPT 邮箱账号、登录与注册 API、密码重置、房间号鉴权、WebSocket TCP 中继，以及后台邮箱配置、用户状态和房间管理。

- 升级前备份目录 `/opt/craftpilot/site/backups/auth-rooms-20261003-235803`，并已执行数据库备份。
- 保留旧镜像 `craftpilot-site:pre-auth-rooms` 和旧容器 `craftpilot-site-pre-rooms-20261004`；旧容器停用自动重启，防止与现服务同时读写同一数据库。
- 新镜像 `craftpilot-site:rooms`，原网络、数据卷、HTTPS 入口和管理员密码保持不变。
- `TRUSTED_PROXY_IP=172.18.0.3` 仅信任当前 Caddy 代理地址；代理容器重建后需核对并同步该值。
- SMTP 密钥持久化在数据库卷 `/app/data/mail-key`，权限 600；`backup.sh` 同时备份密钥。数据库恢复必须配套恢复密钥。
- 未配置 SMTP；未创建生产测试用户、虚假房间或更新记录。邮箱注册在配置并启用后开放。
- 仅使用原 HTTPS 443 转发 WebSocket，未新增游戏公网端口或修改 SSH、防火墙设置。

2026-10-04 邮箱后台增加 QQ、网易 163、126、Yeah 和自定义 SMTP 选择，内置主机、465 端口与 TLS 设置。更换 SMTP 主机或邮箱账号时必须重新填写授权码，旧凭据不跨服务商复用。

- 更新前备份：`/opt/craftpilot/site/backups/mail-presets-20261004-003525`。
- 当前网站镜像：`craftpilot-site:mail-presets`；回退镜像：`craftpilot-site:pre-mail-presets`；停止的旧容器：`craftpilot-site-pre-mail-presets-20261004`。
- 本次没有保存真实邮箱凭据或启用邮件服务，原数据库与密码保留。

2026-10-04 CPT 普通账号密码规则更新为 6 至 12 位（包含两端），注册、登录和密码重置统一校验。管理员密码及 SMTP 授权码规则不变，已有密码摘要不修改；旧密码超出范围的用户需通过找回密码重置。

- 当前镜像：`craftpilot-site:password-policy`，回退镜像：`craftpilot-site:pre-password-policy`。
- 更新前备份：`/opt/craftpilot/site/backups/password-policy-20261004-005119`。
- 保留现有已启用的邮件配置，未发送测试邮件或创建测试账号。线上 5/13 位拒绝、6/12 位进入正常鉴权均已验证。

## 后台管理扩展

2026-10-04 部署用户管理、在线房间详情与管理操作审计：搜索筛选分页、用户备注与会话撤销、批量账号状态、锁房与容量、成员踢出及加入限制、连接断开和流量计数。

- 升级前备份：`/opt/craftpilot/site/backups/management-20261004-010822`，数据库备份已执行。
- 当前镜像：`craftpilot-site:management`；回退镜像：`craftpilot-site:pre-management`；旧容器：`craftpilot-site-pre-management-20261004`，停用自动重启。
- 数据库迁移只增补字段、索引与审计表，保留原用户、SMTP 密钥与配置、管理员密码。
- 部署前运行中的房间为 0，未中断真实房间。生产用户与管理接口只读验证，未使用真实用户测试停用、踢出或批量操作。
- 本地隔离数据验证 32 位用户分页、带成员与 TCP 连接的房间详情、手机弹窗和权限变化；审计和管理备注的重启持久化已验证。

## 联机大厅与分享

2026-10-04 新增登录后公开房间大厅、完整房间号搜索、可选房间密码、房主公开设置和随机邀请链接。分享页面为 `/room`，链接凭证在 `#` 片段，不进入访问日志。

- 更新前备份：`/opt/craftpilot/site/backups/lobby-20261004-092656`。
- 网站镜像：`craftpilot-site:lobby`；回退镜像：`craftpilot-site:pre-lobby`；旧容器：`craftpilot-site-pre-lobby-20261004`。
- 部署前活动房间为 0，原用户、SMTP 设置、管理员密码与数据库卷保留，生产环境不创建演示账号。
- 大厅和免房间密码邀请均要求 CPT 登录，邀请不绕过管理员锁房、人数上限或踢人限制。房间详情不暴露密码摘要和邀请凭证。

## 用户统计口径

2026-10-04 后台首页由匿名设备统计改为 CPT 注册用户统计，按账号 ID 去重，提供总注册数、24 小时与 30 天活跃用户、账号状态、每日新增/活跃。

- 升级前备份：`/opt/craftpilot/site/backups/user-stats-20261004-094729`。
- 当前镜像：`craftpilot-site:user-stats`，回退镜像：`craftpilot-site:pre-user-stats`，旧容器：`craftpilot-site-pre-user-stats-20261004`。
- 新增 `users.last_activity`、`user_daily_activity` 和迁移标记，不删除用户、密码、SMTP 配置或匿名安装表。原设备历史不转换为用户数。
- 历史日活只从现有账号最近活动时间迁移一个有依据的日期，缺失历史不补造；随后登录、鉴权与房间心跳均按用户去重记录。

## Minecraft 游戏域名

2026-10-04 已部署 `前缀.cpts.pw` 游戏入口，可直接填入 Minecraft Java“添加服务器”，不使用网页邀请链接。

- 已确认 `*.cpts.pw` A 记录为 `103.116.247.92`，公网 TCP 25565 可达，未知前缀被拒绝。
- 当前镜像：`craftpilot-site:game-addresses`；回退镜像：`craftpilot-site:pre-game-addresses`。
- 停止的旧容器：`craftpilot-site-pre-game-addresses-20261004`，已关闭其自动重启，避免同时读写数据库。
- 更新前文件备份：`/opt/craftpilot/site/backups/game-addresses-20261004-102036`，数据库及邮件密钥另由 `backup.sh` 备份。
- `.env` 已持久化 `MC_GATEWAY_ENABLED=true`、`MC_GATEWAY_PORT=25565`、`TRUSTED_PROXY_IP=172.18.0.3`，权限 600。
- 当前容器发布 `-p 25565:25565`，继续使用 `craftpilot-data` 卷、`craftpilot` 网络、原管理员密码及 `unless-stopped`。HTTP 3210 未开放公网，Caddy 与证书未改变。
- 新增 `game_addresses` 表，仅保存前缀、账号归属及申请时间。升级前活动房间为 0，原有 1 个账号及 SMTP 配置保留，生产未创建测试账号或前缀。
- 每账号最多两个、全局唯一、随机申请、释放、数据库重新打开后的持久化均已通过测试；服务重启、后台账号/房间/邮箱接口、官网页面及公网端口验证通过。
- 本地隔离验证真实 .NET 房主转发、Minecraft 1.16.5 状态查询、压缩登录和加密双向 48 KB 数据。不是两台真实 Minecraft 客户端的实战验证。

更新时仍须保留原 `.env` 与数据卷，并重新创建带 TCP 25565 映射的容器；仅重启不能补上端口映射。回退前先备份数据库并停止新容器，旧镜像不会使用新增前缀表；不得删除数据卷或让新旧容器同时使用该卷。

## 网页邀请唤醒

2026-10-04 更新 `/room` 邀请页，加入明确点击的 `cpt://` 启动按钮，保留复制 HTTPS 邀请和下载入口。客户端新增当前用户协议注册、启动参数校验、已运行实例邀请转交、登录后继续加入及当前房间身份显示。

- 静态页面更新前备份：`/opt/craftpilot/site/backups/web-invite-20261004-115218`。
- 更新 `public/room.html`、`public/room-link.js` 后同步到运行容器，无需重启或改动数据库、账号、SMTP、Caddy 和游戏端口。
- 后续重建使用镜像 `craftpilot-site:web-invite`（从 Dockerfile 构建），运行容器仍标识为 `craftpilot-site:game-addresses`，但页面已同步更新。
- 邀请参数仍位于 URL 片段，网页不向后端提交邀请凭证。未安装新版客户端时不能保证浏览器能唤醒 CPT，复制邀请作为回退方式。
- 验证覆盖网页按钮/无效邀请、同用户管道交付、冷启动参数、真实页面等待登录与邀请加入状态，以及已有房间显示。浏览器系统确认与 Windows 实际协议启动依赖用户设备，不宣称已覆盖所有浏览器的真实启动行为。

## 邀请失效检查

2026-10-04 新增匿名邀请摘要检查接口，网页仅在房间存在、房主在线且邀请有效时显示唤醒操作；房间关闭后原分享链接不可加入，网页打开/每 15 秒/返回焦点时提示失效。重新创建房间不恢复旧邀请。

- 更新前文件及数据库备份：`/opt/craftpilot/site/backups/invite-expiry-20261004-120559` 与 `backup.sh` 生成的数据库/邮件密钥备份。
- 当前镜像：`craftpilot-site:invite-expiry`；回退镜像：`craftpilot-site:pre-invite-expiry`；停止的旧容器：`craftpilot-site-pre-invite-expiry-20261004`，关闭自动重启。
- 切换前再次验证活动房间为 0；保留 `.env`、数据库卷、账号、SMTP、Caddy 和 TCP 25565 配置，不创建生产测试账号或房间。
- 18 项后端/网页测试通过，包括主动关闭、房主断线、邀请轮换、重新建房及未用加入票据失效；.NET 邀请加入和加密 Minecraft 中继回归通过。
- 网页接口仅接收摘要，拒绝无效格式；有来源检查、限流、无缓存响应，原始邀请凭证仍留在浏览器片段。
