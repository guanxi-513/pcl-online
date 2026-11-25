using System.Net.Http;

namespace PCLCS.Server;

public static class CommunityClient {
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static string ValidateBaseUrl(string url) {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            (uri.Scheme != "https" && uri.Scheme != "http")) throw new ArgumentException("Use an HTTPS or HTTP website URL.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    public static string ValidateDownload(string value) {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Host != "pan.xunlei.com") throw new ArgumentException("Updates must use an HTTPS pan.xunlei.com share link.");
        return uri.AbsoluteUri;
    }

    public static async Task<JObject?> Latest(string baseUrl, string channel) {
        using var response = await Client.GetAsync(ValidateBaseUrl(baseUrl) + "/api/releases/latest?channel=" + Uri.EscapeDataString(channel)).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        var result = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        ValidateDownload(result["url"]!.Value<string>()!);
        return result;
    }

    public static async Task Heartbeat(string baseUrl, string installationId, string version, string channel) {
        // Deliberately exclude accounts, server profiles, hardware identifiers and OS details.
        var body = new JObject { ["installationId"] = installationId, ["version"] = version, ["channel"] = channel };
        using var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync(ValidateBaseUrl(baseUrl) + "/api/telemetry/heartbeat", content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
