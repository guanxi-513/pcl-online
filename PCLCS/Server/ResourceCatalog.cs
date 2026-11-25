using System.Net.Http;

namespace PCLCS.Server;

public static class ResourceCatalog {
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(45) };

    private static async Task<JToken> Get(string url) {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("CPT-ServerManager/1.0");
        using var response = await Client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JToken.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    public static async Task<JArray> Search(string query, string category, string core, string version) {
        var loader = category == "plugins" ? "paper" : core.ToLowerInvariant();
        if (category == "plugins" && core != "Paper" || category == "mods" && core is not ("Fabric" or "Forge" or "NeoForge")) throw new ArgumentException("Paper supports plugins; Fabric/Forge/NeoForge support mods.");
        var facets = new JArray(new JArray("categories:" + loader), new JArray("versions:" + version), new JArray("server_side:required", "server_side:optional"), new JArray("project_type:mod"));
        var result = await Get("https://api.modrinth.com/v2/search?limit=20&query=" + Uri.EscapeDataString(query) + "&facets=" + Uri.EscapeDataString(facets.ToString(Formatting.None))).ConfigureAwait(false);
        return (JArray)result["hits"]!;
    }

    public static async Task<JObject> Resolve(string projectId, string core, string version) {
        var loaders = new JArray(core == "Paper" ? "paper" : core.ToLowerInvariant());
        var versions = new JArray(version);
        var project = await Get("https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(projectId)).ConfigureAwait(false);
        if (project["server_side"]?.Value<string>() is not ("required" or "optional")) throw new InvalidOperationException("This resource does not support server-side use.");
        var result = (JArray)await Get("https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(projectId) + "/version?loaders=" + Uri.EscapeDataString(loaders.ToString(Formatting.None)) + "&game_versions=" + Uri.EscapeDataString(versions.ToString(Formatting.None))).ConfigureAwait(false);
        var entry = result.FirstOrDefault(v => v["version_type"]?.Value<string>() == "release") ?? result.FirstOrDefault() ?? throw new InvalidOperationException("No compatible version.");
        var files = (JArray)entry["files"]!;
        var file = files.FirstOrDefault(f => f["primary"]?.Value<bool>() == true) ?? files.First();
        return new JObject { ["name"] = file["filename"], ["url"] = file["url"], ["sha512"] = file["hashes"]!["sha512"], ["version"] = entry["name"], ["dependencies"] = entry["dependencies"] };
    }
}
