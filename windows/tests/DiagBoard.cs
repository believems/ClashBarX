using System.Text.Json;
using ClashBarX.Core.Json;
using ClashBarX.Core.Models;
using ClashBarX.Core.Proxies;

public static class DiagBoard
{
    public static string Run()
    {
        string json = System.IO.File.ReadAllText(@"C:\Users\XiangLi\AppData\Local\Temp\proxies-live.json");
        var response = JsonSerializer.Deserialize<ProxyGroupsResponse>(json, JsonSharedOptions.Json);
        if (response == null) return "deserialization returned null";
        var board = ProxyBoardService.BuildBoard(response, null);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"groups={board.Groups.Count}");
        foreach (var g in board.Groups)
        {
            sb.AppendLine($"  {g.Name}: now={g.Now}, nodes=[{string.Join(",", g.All)}]");
        }
        return sb.ToString();
    }
}
