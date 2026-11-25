using Renci.SshNet;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PCLCS.Server;

public sealed class ServerProfile {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Minecraft";
    public string Host { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string User { get; set; } = "root";
    public bool Windows { get; set; }
    public bool UseSu { get; set; }
    public string ElevationMethod { get; set; } = "sudo";
    [JsonIgnore] public string ActiveElevation { get; set; } = "";
    [JsonIgnore] public string RootPassword { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Root { get; set; } = "/opt/pcl-minecraft";
    public string GameAddress { get; set; } = "";
    public string Core { get; set; } = "Vanilla";
    public string Version { get; set; } = "1.21.1";
    public int Memory { get; set; } = 4096;
    public int GamePort { get; set; } = 25565;
    public int JavaMajor { get; set; }
    public string JavaPath { get; set; } = "";
    public string LoaderVersion { get; set; } = "";
    public bool Deployed { get; set; }
    public string ClientInstance { get; set; } = "";
    public string ClientRevision { get; set; } = "";
    public string ClientPack { get; set; } = "";

    public void Validate() {
        if (string.IsNullOrWhiteSpace(Host) || Host.Any(char.IsWhiteSpace) || Host.Contains("/") || Host.Contains("\\")) throw new ArgumentException("Invalid SSH host.");
        if (SshPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(User)) throw new ArgumentException("Invalid SSH port or username.");
        if (string.IsNullOrWhiteSpace(Root)) throw new ArgumentException("A dedicated absolute server directory is required.");
    }
}

public sealed class HostKeyConfirmationException(string fingerprint) : Exception("SSH host key confirmation required: " + fingerprint) {
    public string Fingerprint { get; } = fingerprint;
}

public sealed class RootPrivilegeRequiredException(string method = "su") : Exception(method == "sudo" ? "需要当前 SSH 用户的 sudo 密码以取得 root 权限。" : "当前 SSH 用户不是 root，需要提供 root 密码通过 su - 切换。") {
    public string Method { get; } = method;
}

public static class ServerManager {
    private static ConnectionInfo Connection(ServerProfile profile, string password, List<IDisposable> owned) {
        profile.Validate();
        if (password.Length == 0) throw new ArgumentException("Enter an SSH password.");
        AuthenticationMethod auth = new PasswordAuthenticationMethod(profile.User, password);
        return new ConnectionInfo(profile.Host, profile.SshPort, profile.User, auth) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private static void Connect(BaseClient client, ServerProfile profile) {
        string? observed = null;
        client.HostKeyReceived += (_, e) => {
            using var sha = SHA256.Create();
            observed = "SHA256:" + Convert.ToBase64String(sha.ComputeHash(e.HostKey)).TrimEnd('=');
            e.CanTrust = string.Equals(profile.Fingerprint, observed, StringComparison.Ordinal);
        };
        try {
            client.Connect();
        } catch when (observed != null && !string.Equals(profile.Fingerprint, observed, StringComparison.Ordinal)) {
            throw new HostKeyConfirmationException(observed!);
        }
    }

    public static string CheckConnection(ServerProfile profile, string password, bool requireRoot = false) {
        var owned = new List<IDisposable>();
        try {
            using var client = new SshClient(Connection(profile, password, owned));
            Connect(client, profile);
            using var probe = client.CreateCommand("uname -s");
            probe.CommandTimeout = TimeSpan.FromSeconds(20);
            var system = probe.Execute().Trim();
            if (probe.ExitStatus == 0 && system == "Linux" && requireRoot) EnsureRoot(client, profile);
            return probe.ExitStatus == 0 ? system : Execute(client, PowerShell("[Environment]::OSVersion.VersionString"));
        } finally {
            foreach (var resource in owned) resource.Dispose();
        }
    }

    private static void RequireRoot(string uid) {
        if (uid.Trim() != "0") throw new RootPrivilegeRequiredException();
    }

    private static void EnsureRoot(SshClient client, ServerProfile profile) {
        var uid = Execute(client, "id -u");
        if (uid.Trim() == "0") { profile.UseSu = false; profile.ActiveElevation = ""; return; }
        if (!Regex.IsMatch(uid.Trim(), @"\A[0-9]+\z")) throw new InvalidDataException("无法判断远端用户 UID。");
        if (profile.ElevationMethod is not ("auto" or "su" or "sudo")) throw new ArgumentException("无效的 Linux 提权方式。");
        var method = profile.ActiveElevation.Length > 0 ? profile.ActiveElevation : profile.ElevationMethod;
        if (method != "su") {
            using var probe = client.CreateCommand("sudo -n -u root -- id -u");
            probe.CommandTimeout = TimeSpan.FromSeconds(15);
            var result = probe.Execute();
            if (probe.ExitStatus == 0 && result.Trim() == "0") {
                profile.ActiveElevation = "sudo-n"; profile.UseSu = true; return;
            }
            if (method == "sudo-n") method = "sudo";
        }
        if (method == "auto") {
            using var probe = client.CreateCommand("command -v sudo >/dev/null 2>&1");
            probe.CommandTimeout = TimeSpan.FromSeconds(15); probe.Execute();
            method = probe.ExitStatus == 0 ? "sudo" : "su";
        }
        if (string.IsNullOrEmpty(profile.RootPassword)) throw new RootPrivilegeRequiredException(method);
        RequireRoot(SuRootShell.Run(client, "id -u", profile.RootPassword, sudo: method == "sudo"));
        profile.ActiveElevation = method;
        profile.UseSu = true;
    }

    private static string ExecuteRemote(SshClient client, ServerProfile profile, string command, bool pauseable = false) {
        if (profile.Windows || !profile.UseSu || Execute(client, "id -u") == "0") return Execute(client, command, pauseable);
        if (profile.ActiveElevation == "sudo-n") return Execute(client, "sudo -n -u root -- /bin/sh -c " + SuRootShell.Quote("test \"$(id -u)\" = 0 || exit 77; " + command), pauseable);
        var method = profile.ActiveElevation.Length > 0 ? profile.ActiveElevation : profile.ElevationMethod == "sudo" ? "sudo" : "su";
        if (string.IsNullOrEmpty(profile.RootPassword)) throw new RootPrivilegeRequiredException(method);
        return SuRootShell.Run(client, command, profile.RootPassword, pauseable, sudo: method == "sudo");
    }

    public static JObject DeployAndStart(ServerProfile profile, string password, JObject request, Action<string> progress, string? localPack = null, Action<DeploymentUpdate>? update = null, DeploymentControl? control = null, Action<JObject>? installed = null) {
        control?.Checkpoint();
        var owned = new List<IDisposable>();
        try {
            progress("1/5 正在连接服务器并检测系统……");
            using var ssh = new SshClient(Connection(profile, password, owned));
            Connect(ssh, profile);
            using (var probe = ssh.CreateCommand("uname -s")) {
                probe.CommandTimeout = TimeSpan.FromSeconds(20);
                var output = probe.Execute().Trim();
                if (probe.ExitStatus == 0 && output != "Linux") throw new InvalidOperationException("仅支持 Linux 或 Windows 服务器，当前系统：" + output);
                profile.Windows = probe.ExitStatus != 0;
            }
            if (!profile.Windows) EnsureRoot(ssh, profile);
            if (!profile.Windows) progress(profile.UseSu ? "1/5 已通过 " + profile.ActiveElevation + " 验证 root 权限……" : "1/5 已确认 root 权限，部署命令将直接执行……");
            if (profile.Root.StartsWith("~/")) {
                var home = Execute(ssh, profile.Windows ? PowerShell("[Console]::Write($env:USERPROFILE)") : "printf '%s' \"$HOME\"");
                profile.Root = home.TrimEnd('/', '\\') + (profile.Windows ? "\\CraftPilot\\server" : "/CraftPilot/server");
            }
        } finally {
            foreach (var resource in owned) resource.Dispose();
        }
        bool continuing = request["action"]?.Value<string>() == "deploy-resume";
        progress(continuing ? "2/5 正在读取上次部署记录并校验已有安装……" : "2/5 正在补全环境、保护端口并下载 Java 与服务端……");
        var deployed = Run(profile, password, request, localPack, update, control);
        control?.Checkpoint();
        profile.Core = deployed["core"]!.Value<string>()!;
        profile.Version = deployed["version"]!.Value<string>()!;
        profile.GamePort = deployed["port"]!.Value<int>();
        profile.Memory = deployed["memory"]!.Value<int>();
        profile.LoaderVersion = deployed["loaderVersion"]?.Value<string>() ?? "";
        profile.Deployed = true;
        deployed["windows"] = profile.Windows;
        deployed["root"] = profile.Root;
        installed?.Invoke(deployed);
        progress("3/5 安装完成，正在检查和启动服务器……");
        try {
            var state = continuing && deployed["resumedInstalled"]?.Value<bool>() == true ? Run(profile, password, new JObject { ["action"] = "status" }) : null;
            if (state == null || state["state"]?.Value<string>() == "stopped") {
                Run(profile, password, new JObject { ["action"] = "start" }, update: update, control: control);
            } else {
                update?.Invoke(new DeploymentUpdate { Message = "检测到已有运行或启动中的进程，不重复启动，不中断现有连接" });
            }
        } catch {
            ReportStartupDiagnostics(profile, password, update);
            throw;
        }
        progress("4/5 正在等待 Minecraft 完成启动……");
        var deadline = DateTime.UtcNow.AddMinutes(4);
        long logOffset = 0;
        while (DateTime.UtcNow < deadline) {
            var beforePause = DateTime.UtcNow;
            control?.Checkpoint();
            deadline += DateTime.UtcNow - beforePause;
            Thread.Sleep(3000);
            var status = Run(profile, password, new JObject { ["action"] = "status" });
            if (update != null) {
                var logs = Run(profile, password, new JObject { ["action"] = "deployment-logs", ["offset"] = logOffset });
                logOffset = logs["offset"]!.Value<long>();
                foreach (var line in (logs["text"]?.Value<string>() ?? "").Split('\n')) if (line.Trim().Length > 0) update(new DeploymentUpdate { Kind = "terminal", Message = line.TrimEnd('\r') });
            }
            if (status["online"]?.Value<bool>() == true) {
                progress("5/5 正在生成客户端整合包……");
                var client = Run(profile, password, new JObject { ["action"] = "client-pack" }, update: update, control: control);
                deployed["client"] = client;
                progress("5/5 服务端已运行，正在同步客户端配置包……");
                deployed["windows"] = profile.Windows;
                deployed["root"] = profile.Root;
                return deployed;
            }
            if (status["state"]?.Value<string>() == "stopped") {
                var reason = ReportStartupDiagnostics(profile, password, update);
                throw new InvalidOperationException("Server exited. 安装已完成，但服务端启动后退出。" + reason + " 部署文件已保留，可在服务器管理中查看日志并重新启动，无需重新部署。");
            }
        }
        ReportStartupDiagnostics(profile, password, update);
        throw new TimeoutException("启动等待超时；部署文件已保留，请到运行状态页查看日志。");
    }

    private static string ReportStartupDiagnostics(ServerProfile profile, string password, Action<DeploymentUpdate>? update) {
        try {
            var diagnostic = Run(profile, password, new JObject { ["action"] = "startup-diagnostics" });
            foreach (var key in new[] { "text", "crash" }) {
                foreach (var line in (diagnostic[key]?.Value<string>() ?? "").Split('\n')) if (!string.IsNullOrWhiteSpace(line)) update?.Invoke(new DeploymentUpdate { Kind = "terminal", Message = line.TrimEnd('\r') });
            }
            return diagnostic["summary"]?.Value<string>() ?? "";
        } catch {
            update?.Invoke(new DeploymentUpdate { Message = "自动读取启动诊断失败，请在服务器管理中读取 cpt-console.log 或 crash-reports。" });
            return "暂未取得启动原因，请读取服务器控制台日志。";
        }
    }

    public static string PowerShell(string code) => "powershell.exe -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(code));
    private static string Execute(SshClient client, string command, bool pauseable = false) {
        using var task = client.CreateCommand(command);
        task.CommandTimeout = pauseable ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(20);
        var output = task.Execute();
        if (task.ExitStatus != 0) {
            // Agent errors are structured; avoid echoing commands which contain configuration.
            try {
                var result = JObject.Parse(output.Trim());
                if (result["ok"]?.Value<bool>() == false) throw new InvalidOperationException(result["error"]?.Value<string>());
            } catch (JsonReaderException) { }
            throw new InvalidOperationException("Remote operation failed: " + task.Error.Trim());
        }
        return output.Trim();
    }

    private static Stream Embedded(string name) => typeof(ServerManager).Assembly.GetManifestResourceStream("PCLCS.Server." + name)
        ?? throw new FileNotFoundException("Missing deployment resource: " + name);

    private static string Prepare(SshClient ssh, ConnectionInfo connection, ServerProfile profile, bool bootstrap, string requestName, byte[] payload, string? eventName = null, string? controlName = null) {
        Execute(ssh, profile.Windows ? PowerShell("New-Item -ItemType Directory -Path (Join-Path $env:USERPROFILE '.cpt-server') -Force | Out-Null") : "mkdir -p -- \"$HOME/.cpt-server\" && chmod 700 \"$HOME/.cpt-server\"");
        using var sftp = new SftpClient(connection);
        Connect(sftp, profile);
        var directory = sftp.WorkingDirectory.TrimEnd('/') + "/.cpt-server";
        if (!sftp.Exists(directory)) throw new InvalidOperationException("SFTP home must match the SSH user's home directory.");
        if (eventName != null) {
            using var events = new MemoryStream();
            sftp.UploadFile(events, directory + "/" + eventName, false);
            if (!profile.Windows) sftp.ChangePermissions(directory + "/" + eventName, 384);
        }
        if (controlName != null) {
            using var control = new MemoryStream(Encoding.ASCII.GetBytes("run"));
            sftp.UploadFile(control, directory + "/" + controlName, false);
            if (!profile.Windows) sftp.ChangePermissions(directory + "/" + controlName, 384);
        }
        foreach (var name in new[] { "agent.py", "network.py", "server_properties.py", profile.Windows ? "bootstrap.ps1" : "bootstrap.sh" }) {
            using var input = Embedded(name);
            var temporary = directory + "/" + name + "." + Guid.NewGuid().ToString("N") + ".tmp";
            sftp.UploadFile(input, temporary, false);
            if (sftp.Exists(directory + "/" + name)) sftp.DeleteFile(directory + "/" + name);
            sftp.RenameFile(temporary, directory + "/" + name);
        }
        using (var stream = new MemoryStream(payload)) sftp.UploadFile(stream, directory + "/" + requestName, false);
        if (!profile.Windows) sftp.ChangePermissions(directory + "/" + requestName, 384);
        if (bootstrap || profile.Windows) ExecuteRemote(ssh, profile, profile.Windows ? PowerShell("& ([ScriptBlock]::Create([IO.File]::ReadAllText((Join-Path $env:USERPROFILE '.cpt-server/bootstrap.ps1')))); if ($LASTEXITCODE) { exit $LASTEXITCODE }") : "bash " + SuRootShell.Quote(directory + "/bootstrap.sh"));
        return directory + "/" + requestName;
    }

    public static JObject Run(ServerProfile profile, string password, JObject request, string? localUpload = null, Action<DeploymentUpdate>? update = null, DeploymentControl? control = null) {
        control?.Checkpoint();
        var owned = new List<IDisposable>();
        try {
            var connection = Connection(profile, password, owned);
            using var ssh = new SshClient(connection);
            Connect(ssh, profile);
            var action = request["action"]?.Value<string>();
            if (!profile.Windows && (action is "deploy" or "deploy-resume" || profile.UseSu)) EnsureRoot(ssh, profile);
            request["root"] = profile.Root;
            var requestName = "request-" + Guid.NewGuid().ToString("N") + ".json";
            string? remoteRequest = null;
            string? uploaded = null;
            var eventName = update == null && control == null ? null : "events-" + Guid.NewGuid().ToString("N") + ".jsonl";
            var controlName = control == null ? null : "control-" + Guid.NewGuid().ToString("N") + ".txt";
            string? controlPath = null;
            string? eventPath = null;
            using var eventStop = new ManualResetEvent(false);
            Thread? eventThread = null;
            if (eventName != null) request["progressFile"] = eventName;
            if (controlName != null) request["controlFile"] = controlName;
            try {
                if (localUpload != null) {
                    var file = new FileInfo(localUpload);
                    bool serverPack = action == "deploy" && request["pack"] is JObject;
                    long maximum = serverPack ? ServerPack.MaximumUpload : 512L * 1024 * 1024;
                    if (!file.Exists || file.Length > maximum || file.Length == 0 || (serverPack ? file.Extension.ToLowerInvariant() is not (".zip" or ".jar") : file.Extension.ToLowerInvariant() != ".jar")) throw new InvalidDataException("上传文件类型或大小无效。");
                    using (var archive = System.IO.Compression.ZipFile.OpenRead(localUpload)) {
                        if (archive.Entries.Count == 0) throw new InvalidDataException("JAR 文件为空。");
                    }
                    var token = "import-" + Guid.NewGuid().ToString("N") + file.Extension.ToLowerInvariant();
                    Execute(ssh, profile.Windows ? PowerShell("New-Item -ItemType Directory -Path (Join-Path $env:USERPROFILE '.cpt-server') -Force | Out-Null") : "mkdir -p -- \"$HOME/.cpt-server\" && chmod 700 \"$HOME/.cpt-server\"");
                    using var transfer = new SftpClient(connection);
                    Connect(transfer, profile);
                    uploaded = transfer.WorkingDirectory.TrimEnd('/') + "/.cpt-server/" + token;
                    using (var source = File.OpenRead(localUpload)) {
                        using var sha = SHA256.Create();
                        request["uploadHash"] = BitConverter.ToString(sha.ComputeHash(source)).Replace("-", "").ToLowerInvariant();
                        if (serverPack && request["pack"]!["sha256"]?.Value<string>() != request["uploadHash"]!.Value<string>()) throw new InvalidDataException("开服包在识别后已改变，请重新导入。");
                        source.Position = 0;
                        transfer.UploadFile(source, uploaded, false, bytes => { control?.Checkpoint(); update?.Invoke(new DeploymentUpdate { Kind = "download", Message = "上传开服包", Bytes = (long)bytes, Total = file.Length }); });
                    }
                    request["uploadToken"] = token;
                    if (!profile.Windows) transfer.ChangePermissions(uploaded, 384);
                }
                update?.Invoke(new DeploymentUpdate { Message = "准备远端部署代理与 Python 环境" });
                control?.Checkpoint();
                remoteRequest = Prepare(ssh, connection, profile, action is "deploy" or "deploy-resume" or "network-check" or "network-repair", requestName, Encoding.UTF8.GetBytes(request.ToString(Formatting.None)), eventName, controlName);
                var requestDirectory = remoteRequest.Substring(0, remoteRequest.LastIndexOf('/'));
                var requestHome = requestDirectory.Substring(0, requestDirectory.Length - "/.cpt-server".Length);
                if (controlName != null) controlPath = requestDirectory + "/" + controlName;
                if (eventName != null) {
                    eventPath = requestDirectory + "/" + eventName;
                    eventThread = new Thread(() => {
                        long offset = 0; string pending = "";
                        do {
                        try {
                            using var events = new SftpClient(Connection(profile, password, new List<IDisposable>()));
                            events.ConnectionInfo.Timeout = TimeSpan.FromSeconds(5);
                            events.OperationTimeout = TimeSpan.FromSeconds(3);
                            Connect(events, profile);
                            using var stream = events.OpenRead(eventPath);
                            bool? lastPause = null;
                            bool finishing = false;
                            do {
                                if (controlPath != null && control != null && control.PauseRequested != lastPause) {
                                    lastPause = control.PauseRequested;
                                    var marker = controlPath + ".pause";
                                    if (lastPause == true) {
                                        using var data = new MemoryStream(); events.UploadFile(data, marker, true);
                                        if (!profile.Windows) events.ChangePermissions(marker, 384);
                                    } else if (events.Exists(marker)) events.DeleteFile(marker);
                                }
                                var length = events.GetAttributes(eventPath).Size;
                                if (length > 4 * 1024 * 1024) break;
                                while (offset < length) {
                                    stream.Position = offset;
                                    var buffer = new byte[(int)Math.Min(32768, length - offset)];
                                    var read = stream.Read(buffer, 0, buffer.Length); if (read == 0) break;
                                    offset += read; pending += Encoding.ASCII.GetString(buffer, 0, read);
                                    int newline;
                                    while ((newline = pending.IndexOf('\n')) >= 0) {
                                        var line = pending.Substring(0, newline); pending = pending.Substring(newline + 1);
                                        try {
                                            var message = DeploymentEvents.Parse(line);
                                            if (message.Kind == "paused") control?.Acknowledge(true);
                                            if (message.Kind == "resumed") control?.Acknowledge(false);
                                            update?.Invoke(message);
                                        } catch (JsonException) { } catch (InvalidDataException) { }
                                    }
                                    if (pending.Length > 32768) break;
                                }
                                if (finishing) break;
                                finishing = eventStop.WaitOne(200);
                            } while (true);
                            return;
                        } catch {
                            control?.Acknowledge(false);
                            if (!eventStop.WaitOne(0)) update?.Invoke(new DeploymentUpdate { Message = "日志/暂停控制通道中断，正在重连；暂停状态暂未确认" });
                        }
                        } while (!eventStop.WaitOne(1000));
                    }) { IsBackground = true, Name = "CPT deployment events" };
                    eventThread.Start();
                }
                var command = profile.Windows
                    ? PowerShell("& (Join-Path $env:USERPROFILE '.cpt-server/python/python.exe') (Join-Path $env:USERPROFILE '.cpt-server/agent.py') --request '" + requestName + "'; exit $LASTEXITCODE")
                    : "HOME=" + SuRootShell.Quote(requestHome) + " python3 " + SuRootShell.Quote(requestDirectory + "/agent.py") + " --request '" + requestName + "'";
                var result = ParseAgentResponse(ExecuteRemote(ssh, profile, command, control != null));
                if (result["ok"]?.Value<bool>() != true) throw new InvalidOperationException(result["error"]?.Value<string>() ?? "Remote operation failed.");
                if (result["result"] is not JObject data) throw new InvalidDataException("远端部署代理返回格式不正确，未继续操作。");
                return data;
            } finally {
                eventStop.Set(); eventThread?.Join(5000);
                control?.Acknowledge(false);
                if (eventPath != null && (eventThread == null || !eventThread.IsAlive)) {
                    try { using var cleanup = new SftpClient(connection); Connect(cleanup, profile); if (cleanup.Exists(eventPath)) cleanup.DeleteFile(eventPath); } catch { }
                }
                request.Remove("progressFile");
                request.Remove("controlFile");
                if (controlPath != null && (eventThread == null || !eventThread.IsAlive)) {
                    try { using var cleanup = new SftpClient(connection); Connect(cleanup, profile); foreach (var path in new[] { controlPath + ".pause", controlPath }) if (cleanup.Exists(path)) cleanup.DeleteFile(path); } catch { }
                }
                if (remoteRequest != null) {
                    try {
                        using var cleanup = new SftpClient(connection);
                        Connect(cleanup, profile);
                        if (cleanup.Exists(remoteRequest)) cleanup.DeleteFile(remoteRequest);
                    } catch { /* The agent normally removes the payload before processing. */ }
                }
                if (uploaded != null) {
                    try {
                        using var cleanup = new SftpClient(connection);
                        Connect(cleanup, profile);
                        if (cleanup.Exists(uploaded)) cleanup.DeleteFile(uploaded);
                    } catch { }
                }
            }
        } finally {
            foreach (var resource in owned) resource.Dispose();
        }
    }

    private static JObject ParseAgentResponse(string output) {
        try {
            using var reader = new JsonTextReader(new StringReader(output)) { DateParseHandling = DateParseHandling.None };
            var result = JObject.Load(reader);
            if (reader.Read() || result["ok"]?.Type != JTokenType.Boolean) throw new JsonReaderException("Invalid agent response.");
            return result;
        } catch (JsonException) {
            throw new InvalidDataException("远端部署代理没有返回有效结果，未继续操作。可能有登录提示、终端输出或代理启动错误混入返回内容；请使用此修复版重试，并检查远端 Python 环境。不会把远端原始输出或密码写入错误信息。");
        }
    }

    public static JObject CheckNetwork(ServerProfile profile, string password, string core, bool repair = false) {
        var system = CheckConnection(profile, password);
        if (system != "Linux" && !Regex.IsMatch(system, @"^Microsoft Windows|^\d+\.\d+")) throw new InvalidOperationException("仅支持 Linux 或 Windows 网络检测。");
        profile.Windows = system != "Linux";
        return Run(profile, password, new JObject { ["action"] = repair ? "network-repair" : "network-check", ["core"] = core, ["confirmed"] = repair });
    }

    public static string ValidateGameAddress(ServerProfile profile) {
        var address = string.IsNullOrWhiteSpace(profile.GameAddress) ? profile.Host : profile.GameAddress.Trim();
        if (!Regex.IsMatch(address, @"^(?:[A-Za-z0-9._-]+|\[[A-Fa-f0-9:]+\])(?::[0-9]{1,5})?$")) throw new ArgumentException("Invalid game address. Use host:port, or [IPv6]:port.");
        if (address.StartsWith("[")) {
            if (!address.Contains("]:")) address += ":" + profile.GamePort;
        } else if (!address.Contains(":")) address += ":" + profile.GamePort;
        var port = int.Parse(address.Substring(address.LastIndexOf(':') + 1));
        if (port is < 1 or > 65535) throw new ArgumentException("Invalid game port.");
        return address;
    }

    public static JObject FetchClientPack(ServerProfile profile, string password, string localPath) {
        var metadata = Run(profile, password, new JObject { ["action"] = "client-pack" });
        if (metadata["package"]?.Value<string>() != ".cpt-client.mrpack" || metadata["size"]?.Value<long>() is not (> 0 and <= 33554432)) throw new InvalidDataException("Invalid client package metadata.");
        var owned = new List<IDisposable>();
        var temporary = localPath + ".part";
        string? staged = null;
        try {
            using var client = new SftpClient(Connection(profile, password, owned));
            Connect(client, profile);
            var remote = profile.Root.Replace('\\', '/').TrimEnd('/') + "/.cpt-client.mrpack";
            if (!profile.Windows && profile.UseSu) {
                using var ssh = new SshClient(Connection(profile, password, owned));
                Connect(ssh, profile);
                EnsureRoot(ssh, profile);
                var folder = ExecuteRemote(ssh, profile, "mktemp -d /tmp/cpt-export.XXXXXXXXXXXX");
                if (!Regex.IsMatch(folder, @"\A/tmp/cpt-export\.[A-Za-z0-9]{12}\z")) throw new InvalidDataException("Invalid client export staging directory.");
                staged = folder + "/client.mrpack";
                ExecuteRemote(ssh, profile, "install -m 644 -- " + SuRootShell.Quote(remote) + " " + SuRootShell.Quote(staged) + " && chmod 711 -- " + SuRootShell.Quote(folder));
                remote = staged;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            using (var output = File.Create(temporary)) client.DownloadFile(remote, output);
            if (new FileInfo(temporary).Length != metadata["size"]!.Value<long>()) throw new InvalidDataException("Client package size mismatch.");
            using var sha = SHA256.Create();
            using (var input = File.OpenRead(temporary)) {
                var digest = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                if (digest != metadata["sha256"]?.Value<string>()) throw new InvalidDataException("Client package checksum mismatch.");
            }
            using (var archive = System.IO.Compression.ZipFile.OpenRead(temporary)) {
                if (archive.Entries.Any(entry => entry.FullName is not ("modrinth.index.json" or "craftpilot.client.json"))) throw new InvalidDataException("Unexpected client package contents.");
                var index = archive.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("Missing client manifest.");
                using var reader = new StreamReader(index.Open());
                var manifest = JObject.Parse(reader.ReadToEnd());
                var supported = new HashSet<string> { "minecraft", "forge", "neoforge", "fabric-loader" };
                if (((JObject)manifest["dependencies"]!).Properties().Any(entry => !supported.Contains(entry.Name))) throw new InvalidDataException("Unsupported client loader.");
                foreach (var file in (JArray)manifest["files"]!) {
                    var path = file["path"]!.Value<string>()!;
                    if (!Regex.IsMatch(path, @"\Amods/[A-Za-z0-9_+ -][A-Za-z0-9_.+ -]{0,179}\.jar\z")) throw new InvalidDataException("Unsafe client file path.");
                    if (!Regex.IsMatch(file["hashes"]!["sha1"]!.Value<string>()!, @"\A[0-9a-f]{40}\z")) throw new InvalidDataException("Invalid client file checksum.");
                    foreach (var url in (JArray)file["downloads"]!) {
                        var uri = new Uri(url.Value<string>()!);
                        if (uri.Scheme != "https" || uri.UserInfo.Length > 0) throw new InvalidDataException("Unsafe client download URL.");
                    }
                }
            }
            if (File.Exists(localPath)) File.Delete(localPath);
            File.Move(temporary, localPath);
            return metadata;
        } finally {
            if (staged != null) {
                try {
                    using var cleanup = new SshClient(Connection(profile, password, owned)); Connect(cleanup, profile);
                    ExecuteRemote(cleanup, profile, "rm -f -- " + SuRootShell.Quote(staged) + " && rmdir -- " + SuRootShell.Quote(staged.Substring(0, staged.LastIndexOf('/'))));
                } catch { }
            }
            if (File.Exists(temporary)) File.Delete(temporary);
            foreach (var item in owned) item.Dispose();
        }
    }
}
