using System.Text.Json.Nodes;

var mode = args.FirstOrDefault() ?? "normal";
if (mode == "exit")
{
    Environment.Exit(3);
}

string? line;
while ((line = Console.ReadLine()) is not null)
{
    var request = JsonNode.Parse(line) as JsonObject;
    if (request?["id"] is null)
    {
        continue;
    }

    var id = request["id"]!.GetValue<long>();
    var method = request["method"]?.GetValue<string>();
    if (method == "initialize")
    {
        if (mode == "silent")
        {
            Thread.Sleep(5000);
            continue;
        }

        WriteResult(id, new JsonObject
        {
            ["protocolVersion"] = "2025-06-18"
        });
        continue;
    }

    if (method == "tools/call")
    {
        var tool = request["params"]?["name"]?.GetValue<string>();
        if (mode == "error")
        {
            WriteResult(id, new JsonObject
            {
                ["content"] = new JsonArray(Text("overlay unavailable")),
                ["isError"] = true,
                ["_meta"] = new JsonObject { ["error_code"] = "overlay_not_available" }
            });
            continue;
        }

        if (tool == "nvapp_client_get_applications")
        {
            WriteResult(id, new JsonObject
            {
                ["content"] = new JsonArray(Text("ok")),
                ["structuredContent"] = new JsonObject
                {
                    ["applications"] = new JsonArray(new JsonObject
                    {
                        ["game"] = "Red Dead Redemption 2",
                        ["opsSupported"] = true,
                        ["optimizationProfiles"] = new JsonObject
                        {
                            ["ac"] = new JsonObject
                            {
                                ["currentState"] = "UNOPTIMIZED",
                                ["availablePresets"] = new JsonArray("Quality", "Performance")
                            }
                        }
                    })
                }
            });
            continue;
        }

        WriteResult(id, new JsonObject
        {
            ["content"] = new JsonArray(Text("ok"))
        });
        continue;
    }

    Console.WriteLine(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["message"] = "unsupported" }
    }.ToJsonString());
}

static JsonObject Text(string value) =>
    new() { ["type"] = "text", ["text"] = value };

static void WriteResult(long id, JsonObject result) =>
    Console.WriteLine(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result
    }.ToJsonString());
