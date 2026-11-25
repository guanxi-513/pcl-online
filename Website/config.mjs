// 部署级集中配置。所有对外域名/前缀统一从这里取，避免散落的硬编码。
// 房间地址 = <前缀>.<BASE_DOMAIN>，需通配解析到本机（nip.io 自动把 IP 编进域名，零注册）。
export const BASE_DOMAIN = (process.env.MC_BASE_DOMAIN ?? '186.244.238.232.nip.io').trim().toLowerCase();
