using System.Collections.Generic;
using System.Text.Json;
using SIPSorcery.Net;

namespace RemoteHost.Rtc;

public static class IceServerParser
{
    public static List<RTCIceServer> Parse(JsonElement root)
    {
        var list = new List<RTCIceServer>();
        if (!root.TryGetProperty("iceServers", out var arr)) return list;
        foreach (var item in arr.EnumerateArray())
        {
            var server = new RTCIceServer { urls = item.GetProperty("urls").GetString() ?? "" };
            if (item.TryGetProperty("username", out var u)) server.username = u.GetString();
            if (item.TryGetProperty("credential", out var c)) server.credential = c.GetString();
            list.Add(server);
        }
        return list;
    }
}
