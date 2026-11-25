using System.Text.RegularExpressions;
using System.Diagnostics;
using DnsClient;
using System.Net.Sockets;

namespace PCLCS.Server;

public sealed class PlayServer {
    private static readonly LookupClient Resolver = new(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(2), Retries = 0, UseCache = true });
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Minecraft";
    public string Address { get; set; } = "";

    public (string Host, int Port) Endpoint() {
        if (Address.Length > 260) throw new ArgumentException("服务器地址过长。");
        if (!Regex.IsMatch(Address.Trim(), @"\A(?:[A-Za-z0-9._-]+|\[[A-Fa-f0-9:]+\])(?::[0-9]{1,5})?\z")) throw new ArgumentException("请输入服务器域名或 IP，可在末尾添加端口。IPv6 使用 [地址]:端口。");
        var address = Address.Trim();
        var host = address;
        var port = 25565;
        if (address.StartsWith("[")) {
            var end = address.IndexOf(']');
            host = address.Substring(1, end - 1);
            if (address.Length > end + 1) port = int.Parse(address.Substring(end + 2));
        } else if (address.Contains(":")) {
            var split = address.LastIndexOf(':');
            host = address.Substring(0, split);
            port = int.Parse(address.Substring(split + 1));
        }
        if (port is < 1 or > 65535) throw new ArgumentException("服务器端口必须为 1 到 65535。");
        return (host, port);
    }

    public string GameAddress() {
        var endpoint = Endpoint();
        return (endpoint.Host.Contains(":") ? "[" + endpoint.Host + "]" : endpoint.Host) + ":" + endpoint.Port;
    }

    internal (string Host, int Port) StatusEndpoint(LookupClient? resolver = null) {
        var endpoint = Endpoint();
        if (IPAddress.TryParse(endpoint.Host, out _) || Address.Trim().Contains(":")) return endpoint;
        try {
            var records = (resolver ?? Resolver).Query("_minecraft._tcp." + endpoint.Host, QueryType.SRV).Answers.SrvRecords()
                .Where(record => record.Port > 0 && record.Target.Value.TrimEnd('.').Length > 0)
                .OrderBy(record => record.Priority).ThenByDescending(record => record.Weight).ToList();
            if (records.Count > 0) return (records[0].Target.Value.TrimEnd('.'), records[0].Port);
        } catch (DnsResponseException) { } catch (SocketException) { }
        return endpoint;
    }

    public static JObject Query(PlayServer server, LookupClient? resolver = null) {
        var original = server.Endpoint();
        bool connected = false;
        try {
            var endpoint = server.StatusEndpoint(resolver);
            var elapsed = Stopwatch.StartNew();
            JObject result;
            try { result = McStatusClient.GetStatus(endpoint.Host, endpoint.Port, original.Host, 767, () => connected = true); }
            catch when (connected) { result = McStatusClient.GetStatus(endpoint.Host, endpoint.Port, original.Host, 47, () => connected = true); }
            elapsed.Stop();
            if (result["version"]?["name"]?.Type != JTokenType.String) throw new InvalidDataException("Incomplete server status.");
            var description = result["description"];
            var motd = description?.Type == JTokenType.String ? description.Value<string>() : description?["text"]?.Value<string>();
            return new JObject { ["online"] = true, ["reachable"] = true, ["status"] = "online", ["version"] = result["version"]!["name"], ["players"] = result["players"]?["online"],
                ["maximum"] = result["players"]?["max"], ["latency"] = elapsed.ElapsedMilliseconds, ["motd"] = Regex.Replace(motd ?? "", "\u00a7.", "") };
        } catch {
            return new JObject { ["online"] = false, ["reachable"] = connected, ["status"] = connected ? "status-unavailable" : "unreachable", ["message"] = connected ? "端口可连接，但未取得游戏状态；仍可尝试加入" : "状态查询未连接成功；不代表游戏一定不可加入" };
        }
    }
}
