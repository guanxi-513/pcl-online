using Renci.SshNet;
using System.Text.RegularExpressions;

namespace PCLCS.Server;

internal static class SuRootShell {
    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    internal static string Run(SshClient client, string command, string password, bool pauseable = false, bool sudo = false) {
        if (string.IsNullOrEmpty(password) || password.Any(char.IsControl)) throw new ArgumentException("提权密码不能为空或包含控制字符。");
        using var shell = client.CreateShellStream("xterm", 80, 24, 800, 600, 4096,
            new Dictionary<Renci.SshNet.Common.TerminalModes, uint> { [Renci.SshNet.Common.TerminalModes.ECHO] = 0 });
        var marker = "CPT_" + Guid.NewGuid().ToString("N");
        var start = marker + "_BEGIN";
        var end = marker + "_END";
        // Password is written only after su's prompt with terminal echo disabled.
        var script = "test \"$(id -u)\" = 0 || exit 77; d=$(mktemp -d) || exit 78; " +
            "trap 'rm -f -- \"$d/out\" \"$d/err\"; rmdir -- \"$d\"' EXIT; " +
            "printf '\\n" + start + "\\n'; (" + command + ") >\"$d/out\" 2>\"$d/err\"; rc=$?; " +
            "base64 <\"$d/out\"; printf '\\n" + end + ":%s\\n' \"$rc\"";
        var passwordPrompt = sudo ? marker + "_PASSWORD:" : "Password:";
        var invocation = sudo ? "LC_ALL=C sudo -S -k -p " + Quote(passwordPrompt) + " -u root -- /bin/sh -c " : "LC_ALL=C su - root -s /bin/sh -c ";
        shell.WriteLine("stty -echo; " + invocation + Quote(script) + "; printf '\\n" + end + ":AUTH\\n'");
        var prompt = shell.Expect(new Regex(Regex.Escape(passwordPrompt) + "|(?:\\r?\\n)" + start + "|(?:\\r?\\n)" + end + ":AUTH", RegexOptions.CultureInvariant), TimeSpan.FromSeconds(20), 8192);
        if (prompt == null) throw new InvalidOperationException((sudo ? "sudo" : "su") + " 提权超时：服务器未返回密码提示，请检查提权工具配置。");
        if (prompt.Contains(passwordPrompt)) {
            shell.WriteLine(password);
            prompt = shell.Expect(new Regex("(?:\\r?\\n)" + start + "|(?:\\r?\\n)" + end + ":AUTH", RegexOptions.CultureInvariant), TimeSpan.FromSeconds(30), 8192);
        }
        if (prompt == null || !prompt.EndsWith(start, StringComparison.Ordinal)) throw new InvalidOperationException(sudo ? "sudo 取得 root 权限失败：请检查当前 SSH 用户密码和 sudoers 权限。未继续执行操作。" : "su 切换 root 失败：请检查 root 密码、账号锁定状态及 su/PAM 权限。未继续执行操作。");
        var output = shell.Expect(new Regex("(?:\\r?\\n)" + end + ":([0-9]+)\\r?\\n", RegexOptions.CultureInvariant), pauseable ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(20), -1);
        if (output == null) throw new TimeoutException("root 操作超时，请检查远端任务状态后再重试。");
        var result = ParseOutput(output, end);
        if (result.Status != 0) {
            try {
                var error = JObject.Parse(result.Output);
                if (error["ok"]?.Value<bool>() == false) throw new InvalidOperationException(error["error"]?.Value<string>() ?? "root 操作失败。");
            } catch (JsonReaderException) { }
            // Do not echo the command, shell transcript or credentials in failures.
            throw new InvalidOperationException("root 操作失败（退出码 " + result.Status + "），请检查远端环境和任务状态。");
        }
        return result.Output;
    }

    internal static (string Output, int Status) ParseOutput(string output, string marker) {
        var match = Regex.Match(output, "(?:\\r?\\n)" + Regex.Escape(marker) + ":([0-9]+)\\r?\\n\\z", RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidDataException("root 操作未返回有效的结束标记。");
        try {
            var encoded = output.Substring(0, match.Index).Trim();
            var bytes = Convert.FromBase64String(encoded);
            return (new UTF8Encoding(false, true).GetString(bytes).Trim(), int.Parse(match.Groups[1].Value));
        } catch (Exception error) when (error is FormatException or DecoderFallbackException or OverflowException) {
            throw new InvalidDataException("root 操作返回数据损坏，未继续部署。请检查远端终端输出和系统环境。", error);
        }
    }
}
