namespace PCLCS.Server;

public sealed class DeploymentUpdate {
    public string Kind { get; set; } = "detail";
    public string Message { get; set; } = "";
    public long Bytes { get; set; }
    public long Total { get; set; }
    public double Speed { get; set; }
    public bool Complete { get; set; }
}

internal static class DeploymentEvents {
    internal static DeploymentUpdate Parse(string line) {
        if (line.Length > 32768) throw new InvalidDataException("Deployment event too large.");
        var data = JObject.Parse(line);
        var result = new DeploymentUpdate { Kind = data["kind"]?.Value<string>() ?? "detail", Message = data["message"]?.Value<string>() ?? "", Bytes = data["bytes"]?.Value<long>() ?? 0, Total = data["total"]?.Value<long>() ?? 0, Speed = data["speed"]?.Value<double>() ?? 0, Complete = data["complete"]?.Value<bool>() ?? false };
        if (result.Kind is not ("detail" or "download" or "terminal" or "paused" or "resumed") || result.Bytes < 0 || result.Total < 0 || double.IsNaN(result.Speed) || double.IsInfinity(result.Speed)) throw new InvalidDataException("Invalid deployment event.");
        return result;
    }
}
