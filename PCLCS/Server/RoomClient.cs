using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;

namespace PCLCS.Server;

public sealed class RoomClient : IDisposable {
    private readonly string endpoint;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private CancellationTokenSource? roomCancel;
    private ClientWebSocket? control;
    private TcpListener? listener;
    private readonly ConcurrentDictionary<int, Task> connections = new();
    private int connectionId;
    public string Token { get; private set; } = "";
    public JObject? User { get; private set; }
    public string Code { get; private set; } = "";
    public int LocalPort { get; private set; }
    public bool Hosting { get; private set; }
    public JObject? RoomInfo { get; private set; }
    public string ShareLink { get; private set; } = "";
    public string TargetAddress { get; private set; } = "";
    public bool InRoom => roomCancel != null && !roomCancel.IsCancellationRequested;
    public event Action<string>? StatusChanged;

    public RoomClient(string endpoint) { this.endpoint = CommunityClient.ValidateBaseUrl(endpoint); }

    public async Task<JObject> Request(string route, JObject? body = null, bool authenticated = false, CancellationToken cancel = default) {
        if (!route.StartsWith("/api/", StringComparison.Ordinal)) throw new ArgumentException("Invalid API route.");
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, endpoint + route);
        if (authenticated) {
            if (Token == "") throw new InvalidOperationException("请先登录 CPT。");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        }
        if (body != null) request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        var result = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (!response.IsSuccessStatusCode) {
            if (response.StatusCode == HttpStatusCode.Unauthorized && authenticated) { Token = ""; User = null; StopRoom(); }
            throw new InvalidOperationException(result["error"]?.Value<string>() ?? "请求失败。");
        }
        return result;
    }

    // 昵称即用：不注册、不设密码，服务端换发一个会话 token。
    public async Task SignIn(string nickname) {
        var result = await Request("/api/auth/guest", new JObject { ["nickname"] = nickname }).ConfigureAwait(false);
        AcceptLogin(result);
    }
    private void AcceptLogin(JObject result) {
        string token = result["token"]!.Value<string>()!;
        if (token.Length != 64 || token.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid login token.");
        Token = token; User = (JObject)result["user"]!;
    }
    public async Task Restore(string path) {
        if (!File.Exists(path)) return;
        try {
            Token = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
            User = (JObject)(await Request("/api/auth/me", authenticated: true).ConfigureAwait(false))["user"]!;
        } catch { Token = ""; User = null; }
    }
    public void SaveSession(string path) {
        if (Token == "") { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(Token), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path + ".tmp", data);
        if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
    }
    public async Task Logout() {
        try { if (Token != "") await Request("/api/auth/logout", new JObject(), true).ConfigureAwait(false); }
        finally { Token = ""; User = null; StopRoom(); }
    }

    private async Task<ClientWebSocket> Socket(string ticket, CancellationToken cancel) {
        if (ticket.Length != 64 || ticket.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid connection ticket.");
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("cpt." + ticket);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        var address = new UriBuilder(endpoint) { Scheme = endpoint.StartsWith("https:") ? "wss" : "ws", Path = "/api/rooms/socket" };
        try { await socket.ConnectAsync(address.Uri, cancel).ConfigureAwait(false); return socket; }
        catch { socket.Dispose(); throw; }
    }
    private static async Task<JObject> ReadControl(ClientWebSocket socket, CancellationToken cancel) {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        WebSocketReceiveResult received;
        do {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancel).ConfigureAwait(false);
            if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > 8192) throw new IOException("联机控制连接已关闭。");
            message.Write(buffer, 0, received.Count);
        } while (!received.EndOfMessage);
        return JObject.Parse(Encoding.UTF8.GetString(message.ToArray()));
    }
    public (string Code, string Invite) ParseInvitation(string value) {
        value = value.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Fa-f0-9]{16}$")) return (value.ToUpperInvariant(), "");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.GetLeftPart(UriPartial.Authority) != new Uri(endpoint).GetLeftPart(UriPartial.Authority) || url.AbsolutePath != "/room" || url.Query.Length != 0) throw new ArgumentException("请输入有效的 CPT 房间号或官方邀请链接。");
        var match = System.Text.RegularExpressions.Regex.Match(url.Fragment, "^#([A-F0-9]{16})\\.([a-f0-9]{64})$");
        if (!match.Success) throw new ArgumentException("邀请链接格式无效。");
        return (match.Groups[1].Value, match.Groups[2].Value);
    }
    public async Task<JObject> Lobby(string code = "") {
        var search = code.Trim();
        if (search != "") search = ParseInvitation(search).Code;
        return await Request("/api/rooms/lobby" + (search == "" ? "" : "?code=" + Uri.EscapeDataString(search)), authenticated: true).ConfigureAwait(false);
    }
    public async Task UpdateRoom(string title, bool isPublic, string? password = null, bool rotateInvite = false) {
        if (!Hosting || !InRoom) throw new InvalidOperationException("只有房主可以修改房间设置。");
        var body = new JObject { ["code"] = Code, ["title"] = title, ["public"] = isPublic, ["rotateInvite"] = rotateInvite };
        if (password != null) body["password"] = password;
        RoomInfo = await Request("/api/rooms/settings", body, true).ConfigureAwait(false);
        ShareLink = RoomInfo["shareLink"]?.Value<string>() ?? "";
    }
    public Task<JObject> GameAddresses() => Request("/api/game-addresses", authenticated: true);
    public async Task RefreshRoom() {
        var source = roomCancel;
        var code = Code;
        if (source == null || source.IsCancellationRequested) return;
        var info = await Request("/api/rooms/mine", authenticated: true, cancel: source.Token).ConfigureAwait(false);
        if (!ReferenceEquals(roomCancel, source) || Code != code) return;
        if (info["code"]?.Value<string>() != code) throw new InvalidDataException("房间状态与当前连接不一致。");
        RoomInfo = info;
        ShareLink = Hosting ? info["shareLink"]?.Value<string>() ?? "" : "";
    }
    public Task<JObject> AllocateAddress(string prefix = "") => Request("/api/game-addresses", new JObject { ["prefix"] = prefix.Trim() }, true);
    public async Task<JObject> PublishAddress(string prefix) {
        if (!Hosting || !InRoom) throw new InvalidOperationException("请先创建房间，再发布对应的游戏地址。");
        var target = new PlayServer { Address = TargetAddress }.Endpoint();
        return await Request("/api/rooms/publish-address", new JObject { ["code"] = Code, ["prefix"] = prefix, ["confirmed"] = true, ["targetHost"] = target.Host, ["targetPort"] = target.Port }, true).ConfigureAwait(false);
    }
    public Task<JObject> UnpublishAddress(string prefix) => Request("/api/rooms/unpublish-address", new JObject { ["prefix"] = prefix }, true);
    public async Task ReleaseAddress(string prefix) {
        if (!System.Text.RegularExpressions.Regex.IsMatch(prefix, "^[a-z0-9-]{3,32}$")) throw new ArgumentException("Invalid prefix.");
        using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint + "/api/game-addresses/" + Uri.EscapeDataString(prefix));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false))["error"]?.Value<string>() ?? "释放前缀失败。");
    }
    public async Task Enter(bool host, string code, int port, string title = "", bool isPublic = false, string password = "", string serverAddress = "") {
        if (InRoom) throw new InvalidOperationException("请先退出当前房间。");
        var target = serverAddress == "" ? (Host: "127.0.0.1", Port: port) : new PlayServer { Address = serverAddress }.Endpoint();
        if (host && (target.Port < (serverAddress == "" ? 1024 : 1) || target.Port > 65535)) throw new ArgumentException("请输入有效的 Minecraft 端口。");
        JObject body;
        if (host) {
            body = new JObject { ["port"] = target.Port, ["public"] = isPublic, ["password"] = password, ["targetKind"] = serverAddress == "" ? "lan" : "server" };
            if (title.Trim() != "") body["title"] = title.Trim();
        } else {
            var invitation = ParseInvitation(code);
            body = new JObject { ["code"] = invitation.Code, ["password"] = password };
            if (invitation.Invite != "") body["invite"] = invitation.Invite;
        }
        var result = await Request(host ? "/api/rooms/create" : "/api/rooms/join", body, true).ConfigureAwait(false);
        Code = result["code"]!.Value<string>()!; Hosting = host;
        RoomInfo = result; ShareLink = host ? result["shareLink"]?.Value<string>() ?? "" : "";
        TargetAddress = host ? (target.Host.Contains(":") ? "[" + target.Host + "]" : target.Host) + ":" + target.Port : "";
        var source = new CancellationTokenSource(); roomCancel = source;
        try {
            control = await Socket(result["ticket"]!.Value<string>()!, source.Token).ConfigureAwait(false);
            await ReadControl(control, source.Token).ConfigureAwait(false);
            if (!host) { listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(8); LocalPort = ((IPEndPoint)listener.LocalEndpoint).Port; _ = AcceptGuests(listener, source.Token); }
            _ = ObserveControl(control, host, target.Host, target.Port, source);
            StatusChanged?.Invoke(host ? "房间已创建" : "已加入房间");
        } catch { StopRoom(); try { await Request("/api/rooms/leave", new JObject(), true).ConfigureAwait(false); } catch { } throw; }
    }
    private async Task ObserveControl(ClientWebSocket socket, bool host, string targetHost, int port, CancellationTokenSource source) {
        try {
            while (!source.IsCancellationRequested) {
                var message = await ReadControl(socket, source.Token).ConfigureAwait(false);
                if (message["type"]?.Value<string>() == "connection" && host) Track(HostConnection(message["ticket"]!.Value<string>()!, targetHost, port, source.Token));
                if (message["type"]?.Value<string>() == "members") { if (RoomInfo != null) RoomInfo["members"] = message["members"]!.DeepClone(); StatusChanged?.Invoke("房间人数：" + message["members"]); }
            }
        } catch (Exception ex) { if (!source.IsCancellationRequested) { Logger.Warn("控制连接断开: " + (ex.GetBaseException().Message ?? "未知")); StatusChanged?.Invoke("房间已关闭或连接中断"); } }
        finally { if (ReferenceEquals(roomCancel, source)) { StopRoom(); StatusChanged?.Invoke("已离开联机房间"); } }
    }
    private void Track(Task task) {
        int id = Interlocked.Increment(ref connectionId); connections[id] = task;
        _ = task.ContinueWith(completed => {
            connections.TryRemove(id, out _);
            if (completed.IsFaulted) {
                Logger.Warn("游戏连接失败: " + (completed.Exception?.GetBaseException().Message ?? "未知"));
                StatusChanged?.Invoke("游戏连接失败，请检查房主局域网端口");
            }
        }, TaskScheduler.Default);
    }
    private async Task AcceptGuests(TcpListener acceptor, CancellationToken cancel) {
        try {
            while (!cancel.IsCancellationRequested) {
                var tcp = await acceptor.AcceptTcpClientAsync().ConfigureAwait(false);
                if (connections.Count >= 8) { tcp.Dispose(); continue; }
                Track(GuestConnection(tcp, cancel));
            }
        } catch { }
    }
    private async Task GuestConnection(TcpClient tcp, CancellationToken cancel) {
        using (tcp) {
            var result = await Request("/api/rooms/stream", new JObject { ["code"] = Code }, true, cancel).ConfigureAwait(false);
            using var socket = await Socket(result["ticket"]!.Value<string>()!, cancel).ConfigureAwait(false);
            await ReadControl(socket, cancel).ConfigureAwait(false);
            await Bridge(tcp, socket, cancel, "玩家").ConfigureAwait(false);
        }
    }
    private async Task HostConnection(string ticket, string targetHost, int port, CancellationToken cancel) {
        using var tcp = new TcpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var registration = deadline.Token.Register(tcp.Close);
        await tcp.ConnectAsync(targetHost, port).ConfigureAwait(false);
        using var socket = await Socket(ticket, deadline.Token).ConfigureAwait(false);
        await ReadControl(socket, deadline.Token).ConfigureAwait(false);
        deadline.CancelAfter(Timeout.Infinite);
        await Bridge(tcp, socket, cancel, "房主").ConfigureAwait(false);
    }
    private static async Task Bridge(TcpClient tcp, ClientWebSocket socket, CancellationToken cancel, string role = "") {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var token = stop.Token;
        using var registration = token.Register(() => { tcp.Close(); socket.Abort(); });
        var network = tcp.GetStream();
        async Task Upload() {
            var buffer = new byte[16384];
            while (!token.IsCancellationRequested) {
                int count = await network.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (count == 0) return;
                await socket.SendAsync(new ArraySegment<byte>(buffer, 0, count), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
            }
        }
        async Task Download() {
            var buffer = new byte[16384];
            while (!token.IsCancellationRequested) {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Binary) throw new IOException("Invalid relay data.");
                await network.WriteAsync(buffer, 0, result.Count, token).ConfigureAwait(false);
            }
        }
        var outgoing = Upload(); var incoming = Download();
        try {
            var finished = await Task.WhenAny(outgoing, incoming).ConfigureAwait(false);
            var side = ReferenceEquals(finished, outgoing) ? "上游(本地TCP)" : "下游(中继WS)";
            var tag = role == "" ? "联机隧道" : "联机隧道[" + role + "]";
            if (finished.IsCanceled) {
                if (!cancel.IsCancellationRequested) Logger.Warn(tag + "断开：" + side + "被取消");
            } else if (finished.IsFaulted) {
                Logger.Warn(tag + "断开：" + side + "异常 " + (finished.Exception?.GetBaseException().Message ?? "未知"));
            } else {
                Logger.Info(tag + "正常关闭：" + side + "先结束");
            }
        }
        finally { stop.Cancel(); try { await Task.WhenAll(outgoing, incoming).ConfigureAwait(false); } catch { } }
    }
    public async Task Leave() { try { if (Token != "") await Request("/api/rooms/leave", new JObject(), true).ConfigureAwait(false); } finally { StopRoom(); } }
    public void StopRoom() {
        var source = roomCancel; roomCancel = null;
        source?.Cancel(); listener?.Stop(); listener = null; control?.Abort(); control?.Dispose(); control = null;
        Code = ""; LocalPort = 0; ShareLink = ""; RoomInfo = null; TargetAddress = "";
    }
    public void Dispose() { StopRoom(); http.Dispose(); }
}
