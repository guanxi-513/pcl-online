using System.IO.Compression;
using System.Text.RegularExpressions;

namespace PCLCS.Server;

public static class ServerPack {
    public const long MaximumUpload = 2L * 1024 * 1024 * 1024;
    public static string SafePath(string name) {
        if (string.IsNullOrEmpty(name) || name.Contains("\\") || name.StartsWith("/") ||
            name.Split('/').Any(part => part is "" or "." or ".." || part.Any(c => c < 32 || ":*?\"<>|".Contains(c))))
            throw new InvalidDataException("开服包含有不安全的文件路径。");
        if (name.Split('/').Any(part => part.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase) || part.StartsWith(".cpt-", StringComparison.OrdinalIgnoreCase) || part.EndsWith(".") || part.EndsWith(" ") ||
            Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("开服包含有保留文件名。");
        return name;
    }

    private static string Text(ZipArchiveEntry entry) {
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("开服包配置文件超过 1 MiB。");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    public static JObject Inspect(string path) {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0 || file.Length > MaximumUpload || file.Extension.ToLowerInvariant() is not (".zip" or ".jar"))
            throw new InvalidDataException("请选择不超过 2 GiB 的 ZIP 开服包或服务端 JAR。");
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count == 0 || zip.Entries.Count > 50000) throw new InvalidDataException("开服包为空或文件过多。");
        var result = new JObject { ["core"] = "Vanilla", ["version"] = "", ["memory"] = 4096, ["minMemory"] = 512, ["port"] = 25565,
            ["javaMajor"] = 0, ["loaderVersion"] = "", ["jvmArgs"] = new JArray(), ["launchArgs"] = new JArray(), ["prefix"] = "" };
        using (var source = File.OpenRead(path)) {
            using var sha = System.Security.Cryptography.SHA256.Create();
            result["sha256"] = BitConverter.ToString(sha.ComputeHash(source)).Replace("-", "").ToLowerInvariant();
        }
        if (file.Extension.Equals(".jar", StringComparison.OrdinalIgnoreCase)) {
            result["launchArgs"] = new JArray("-jar", "server.jar", "nogui");
            DetectJar(zip, result);
            if (file.Name.StartsWith("paper", StringComparison.OrdinalIgnoreCase)) result["core"] = "Paper";
            var forge = Regex.Match(file.Name, @"^forge-(1\.[0-9.]+)-([0-9.]+)(?:-universal)?\.jar$", RegexOptions.IgnoreCase);
            if (forge.Success) {
                result["core"] = "Forge";
                result["version"] = forge.Groups[1].Value;
                result["loaderVersion"] = forge.Groups[2].Value;
            }
            Validate(result);
            return result;
        }
        long expanded = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries) {
            var name = entry.FullName.TrimEnd('/');
            SafePath(name);
            if (!names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || entry.Length > MaximumUpload)
                throw new InvalidDataException("开服包含重复文件、符号链接或超大文件。");
            expanded += entry.Length;
        }
        if (expanded > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("开服包解压后超过 4 GiB。");
        var files = zip.Entries.Where(entry => !entry.FullName.EndsWith("/")).ToList();
        string prefix = "";
        while (files.Count > 0 && files.All(entry => entry.FullName.Substring(prefix.Length).Contains("/"))) {
            var first = files[0].FullName.Substring(prefix.Length).Split('/')[0] + "/";
            if (!files.All(entry => entry.FullName.StartsWith(prefix + first, StringComparison.Ordinal))) break;
            prefix += first;
        }
        result["prefix"] = prefix;
        var entries = files.ToDictionary(entry => entry.FullName.Substring(prefix.Length), StringComparer.OrdinalIgnoreCase);
        JObject? explicitConfig = null;
        if (entries.TryGetValue("cpt-server.json", out var manifest)) {
            var config = explicitConfig = JObject.Parse(Text(manifest));
            foreach (var key in new[] { "core", "version", "memory", "minMemory", "port", "javaMajor", "loaderVersion", "launchArgs", "jvmArgs" })
                if (config[key] != null) result[key] = config[key]!.DeepClone();
        }
        if (entries.TryGetValue("server.properties", out var properties)) {
            var match = Regex.Match(Text(properties), @"(?m)^\s*server-port\s*=\s*(\d+)");
            if (match.Success) result["port"] = int.Parse(match.Groups[1].Value);
        }
        if (entries.TryGetValue("fabric-server-launcher.properties", out var fabric)) {
            result["core"] = "Fabric";
            foreach (var (key, target) in new[] { ("gameVersion", "version"), ("loaderVersion", "loaderVersion") }) {
                var match = Regex.Match(Text(fabric), @"(?m)^\s*" + key + @"\s*=\s*([^\r\n]+)");
                if (match.Success) result[target] = match.Groups[1].Value.Trim();
            }
        }
        if (entries.TryGetValue("variables.txt", out var variables)) {
            var text = Text(variables);
            foreach (var (key, target) in new[] { ("MINECRAFT_VERSION", "version"), ("MC_VERSION", "version"), ("MODLOADER_VERSION", "loaderVersion"), ("JAVA_VERSION", "javaMajor") }) {
                var match = Regex.Match(text, @"(?m)^\s*" + key + @"\s*=\s*([^\r\n]+)");
                if (match.Success) result[target] = target == "javaMajor" ? new JValue(int.Parse(match.Groups[1].Value.Trim(' ', '\"', '\''))) : new JValue(match.Groups[1].Value.Trim(' ', '\"', '\''));
            }
            var loader = Regex.Match(text, @"(?m)^\s*MODLOADER\s*=\s*([^\r\n]+)");
            if (loader.Success) {
                string kind = loader.Groups[1].Value.Trim(' ', '\"', '\'');
                result["core"] = new[] { "Vanilla", "Paper", "Fabric", "Forge", "NeoForge" }.FirstOrDefault(value => value.Equals(kind, StringComparison.OrdinalIgnoreCase)) ?? result["core"]!.Value<string>();
            }
        }
        var fabricLibrary = entries.Keys.FirstOrDefault(name => Regex.IsMatch(name, @"^libraries/net/fabricmc/fabric-loader/[^/]+/fabric-loader-[^/]+\.jar$"));
        if (fabricLibrary != null) {
            result["core"] = "Fabric";
            result["loaderVersion"] = fabricLibrary.Split('/')[4];
        }
        var argumentFile = entries.Keys.FirstOrDefault(name => Regex.IsMatch(name, @"^libraries/net/(?:minecraftforge/forge|neoforged/neoforge)/[^/]+/(?:unix|win)_args\.txt$"));
        if (argumentFile != null) {
            bool neo = argumentFile.Contains("/neoforged/");
            result["core"] = neo ? "NeoForge" : "Forge";
            string coordinate = argumentFile.Split('/')[4];
            if (!neo && coordinate.Contains("-")) {
                result["version"] = coordinate.Split('-')[0];
                result["loaderVersion"] = coordinate.Substring(coordinate.IndexOf('-') + 1);
            } else if (neo) {
                result["loaderVersion"] = coordinate;
                var parts = coordinate.Split('.');
                if (parts.Length >= 2) result["version"] = "1." + parts[0] + (parts[1] == "0" ? "" : "." + parts[1]);
            }
            if (((JArray)result["launchArgs"]!).Count == 0) result["launchArgs"] = new JArray("@" + argumentFile, "nogui");
        }
        foreach (var entry in entries.Where(pair => Regex.IsMatch(pair.Key, @"^[^/]+\.(?:bat|cmd|sh)$", RegexOptions.IgnoreCase))) {
            var script = Text(entry.Value);
            var javaLine = script.Split('\n').FirstOrDefault(line => Regex.IsMatch(line.Trim(), "^(?:@?java|\"[^\"]*java(?:.exe)?\"|[^ ]*/java)\\s", RegexOptions.IgnoreCase));
            if (javaLine == null) continue;
            var tokens = Regex.Matches(javaLine, "\"([^\"]*)\"|'([^']*)'|([^\\s]+)").Cast<Match>()
                .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value).Skip(1).ToList();
            if (tokens.Any(token => token.Contains("$") || token.Contains("%") || token is "&" or "&&" or "|" || token.StartsWith(">") || token.StartsWith("<"))) continue;
            foreach (var token in tokens) {
                var memory = Regex.Match(token, @"^-Xm([xs])(\d+)([mMgG])$");
                if (memory.Success) result[memory.Groups[1].Value == "x" ? "memory" : "minMemory"] = checked(int.Parse(memory.Groups[2].Value) * (memory.Groups[3].Value.ToLowerInvariant() == "g" ? 1024 : 1));
            }
            if (((JArray)result["launchArgs"]!).Count == 0) {
                int jar = tokens.IndexOf("-jar");
                if (jar >= 0 && jar + 1 < tokens.Count && entries.ContainsKey(tokens[jar + 1])) {
                    result["launchArgs"] = new JArray("-jar", tokens[jar + 1], "nogui");
                    result["jvmArgs"] = new JArray(tokens.Take(jar).Where(token => !token.StartsWith("-Xm")));
                }
            }
            break;
        }
        if (entries.TryGetValue("user_jvm_args.txt", out var jvm)) {
            var text = Text(jvm);
            var memory = Regex.Match(text, @"-Xmx(\d+)([mMgG])");
            if (memory.Success) result["memory"] = checked(int.Parse(memory.Groups[1].Value) * (memory.Groups[2].Value.ToLowerInvariant() == "g" ? 1024 : 1));
            var initial = Regex.Match(text, @"-Xms(\d+)([mMgG])");
            if (initial.Success) result["minMemory"] = checked(int.Parse(initial.Groups[1].Value) * (initial.Groups[2].Value.ToLowerInvariant() == "g" ? 1024 : 1));
            result["jvmArgs"] = new JArray(text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !line.TrimStart().StartsWith("#")).SelectMany(line => line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Where(token => !token.StartsWith("-Xm")));
        }
        if (((JArray)result["launchArgs"]!).Count == 0) {
            var jars = entries.Keys.Where(name => !name.Contains("/") && name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !name.Contains("installer")).ToList();
            string? jar = jars.FirstOrDefault(name => name == "server.jar") ?? (jars.Count == 1 ? jars[0] : null);
            if (jar == null) throw new InvalidDataException("无法唯一识别启动入口，请在开服包根目录添加 cpt-server.json，指定 launchArgs。");
            result["launchArgs"] = new JArray("-jar", jar, "nogui");
        }
        var args = (JArray)result["launchArgs"]!;
        if (args[0]?.Value<string>() == "-jar" && args.Count >= 2 && entries.TryGetValue(args[1]!.Value<string>()!, out var serverJar)) {
            if (serverJar.Length > 512L * 1024 * 1024) throw new InvalidDataException("服务端 JAR 超过 512 MiB。");
            using var stream = new MemoryStream();
            using (var input = serverJar.Open()) input.CopyTo(stream);
            stream.Position = 0;
            using var nested = new ZipArchive(stream, ZipArchiveMode.Read);
            DetectJar(nested, result);
            if (serverJar.Name.StartsWith("paper", StringComparison.OrdinalIgnoreCase)) result["core"] = "Paper";
            if (serverJar.Name.StartsWith("fabric", StringComparison.OrdinalIgnoreCase)) result["core"] = "Fabric";
            var forge = Regex.Match(serverJar.Name, @"^forge-(1\.[0-9.]+)-([0-9.]+)(?:-universal)?\.jar$", RegexOptions.IgnoreCase);
            if (forge.Success) {
                result["core"] = "Forge";
                result["version"] = forge.Groups[1].Value;
                result["loaderVersion"] = forge.Groups[2].Value;
            }
        }
        if (explicitConfig != null) {
            foreach (var key in new[] { "core", "version", "memory", "minMemory", "port", "javaMajor", "loaderVersion", "launchArgs", "jvmArgs" })
                if (explicitConfig[key] != null) result[key] = explicitConfig[key]!.DeepClone();
        }
        Validate(result);
        var launch = (JArray)result["launchArgs"]!;
        string launchPath = launch[0]!.Value<string>() == "-jar" ? launch[1]!.Value<string>()! : launch[0]!.Value<string>()!.Substring(1);
        if (!entries.ContainsKey(launchPath)) throw new InvalidDataException("指定的开服包启动入口不存在。");
        return result;
    }

    private static void DetectJar(ZipArchive zip, JObject result) {
        var manifest = zip.GetEntry("META-INF/MANIFEST.MF");
        if (manifest != null && Regex.IsMatch(Text(manifest), @"(?im)^Main-Class:\s*(?:io\.papermc|com\.destroystokyo\.paper)\.")) result["core"] = "Paper";
        var version = zip.GetEntry("version.json");
        if (version != null) {
            var info = JObject.Parse(Text(version));
            if (info["id"] != null) result["version"] = info["id"];
            if (info["java_version"] != null) result["javaMajor"] = info["java_version"];
        }
        var versions = zip.GetEntry("META-INF/versions.list");
        if (versions != null && result["version"]!.Value<string>() == "") {
            var fields = Text(versions).Split('\n')[0].Trim().Split('\t');
            if (fields.Length >= 2) result["version"] = fields[1];
        }
    }

    public static void Validate(JObject config) {
        var args = config["launchArgs"] as JArray ?? throw new InvalidDataException("缺少 Java 启动参数。");
        if (args.Count is < 1 or > 32) throw new InvalidDataException("启动参数数量无效。");
        string entry = args[0]!.Value<string>()!;
        if (entry == "-jar") {
            if (args.Count != 3 || args[2]!.Value<string>() != "nogui" || !SafePath(args[1]!.Value<string>()!).EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("JAR 启动入口无效。");
        } else if (args.Count != 2 || args[1]!.Value<string>() != "nogui" || !entry.StartsWith("@libraries/") || !SafePath(entry.Substring(1)).EndsWith("_args.txt"))
            throw new InvalidDataException("仅支持服务端 JAR 或 Forge/NeoForge 参数文件启动。");
        foreach (var token in config["jvmArgs"] as JArray ?? new JArray()) {
            if (!Regex.IsMatch(token.Value<string>()!, @"^-(?:XX:[A-Za-z0-9+_=:.-]+|D[A-Za-z0-9_.-]+=[A-Za-z0-9_.,:/+-]+)$"))
                throw new InvalidDataException("开服包 JVM 参数需要人工确认，仅自动导入 -XX 与 -D 参数。");
        }
        if (config["core"]?.Value<string>() is "Fabric" or "Forge" or "NeoForge" && string.IsNullOrEmpty(config["loaderVersion"]?.Value<string>()))
            throw new InvalidDataException("未识别加载器版本，请在 cpt-server.json 中指定 loaderVersion。");
        int memory = config["memory"]!.Value<int>(), port = config["port"]!.Value<int>();
        int initialMemory = config["minMemory"]?.Value<int>() ?? 512;
        if (memory is < 512 or > 262144 || initialMemory < 128 || initialMemory > memory || port is < 1 or > 65535 || port == 25575) throw new InvalidDataException("开服包内存或端口配置无效。");
    }
}
