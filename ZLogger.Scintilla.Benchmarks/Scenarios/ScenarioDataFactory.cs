using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
namespace ZLogger.Scintilla.Benchmarks.Scenarios;

public sealed class OrderData
{
    public long OrderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string UserId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string[] Items { get; set; } = Array.Empty<string>();
}

public static class ScenarioDataFactory
{
    private static readonly DateTime BaseTimestampUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly LogLevel[] MixedLevelDistribution = BuildLevelDistribution();

    private static readonly string SampleStackTrace =
        """
        System.InvalidOperationException: Connection pool exhausted after 30s timeout.
           at Acme.Data.ConnectionPool.AcquireAsync(CancellationToken ct) in /src/Data/ConnectionPool.cs:line 142
           at Acme.Services.OrderService.PlaceOrderAsync(OrderRequest req) in /src/Services/OrderService.cs:line 87
           at Acme.Api.Controllers.OrderController.Post(OrderRequest req) in /src/Controllers/OrderController.cs:line 34
           at Microsoft.AspNetCore.Mvc.Infrastructure.ActionMethodExecutor.Execute()
           at Microsoft.AspNetCore.Mvc.Infrastructure.ControllerActionInvoker.InvokeActionMethodAsync()
        """;

    public static IReadOnlyList<string> BuildMessages(
        int count,
        int sizeBytes,
        TokenDensity tokenDensity,
        bool structuredPayload,
        int seed = 12345)
    {
        var random = new Random(seed);
        var list = new List<string>(count);
        for (var i = 0; i < count; i++)
            list.Add(BuildSingleMessage(i, sizeBytes, tokenDensity, structuredPayload, random));
        return list;
    }

    public static IReadOnlyList<byte[]> BuildUtf8Messages(IReadOnlyList<string> messages)
    {
        var list = new List<byte[]>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
            list.Add(Encoding.UTF8.GetBytes(messages[i]));
        return list;
    }

    public static IReadOnlyList<OrderData> BuildOrderData(int count, int seed = 54321)
    {
        var random = new Random(seed);
        var list = new List<OrderData>(count);
        var statuses = new[] { "Created", "Processing", "Shipped", "Delivered", "Cancelled" };
        var users = new[] { "user001", "user002", "user003", "user004", "user005" };

        for (var i = 0; i < count; i++)
        {
            list.Add(new OrderData
            {
                OrderId = 10000 + i,
                Status = statuses[i % statuses.Length],
                Timestamp = BaseTimestampUtc.AddSeconds(i),
                UserId = users[i % users.Length],
                Amount = ((i + 1) * 9.99m) + random.Next(0, 25),
                Items = [$"item-{i:D5}", $"item-{(i + 1):D5}", $"item-{(i + 2):D5}"]
            });
        }

        return list;
    }

    /// <summary>
    /// Builds a realistic mixed-content dataset with varied levels, sizes, and content types.
    /// Distribution: Trace 10%, Debug 20%, Info 40%, Warning 20%, Error 8%, Critical 2%.
    /// Content: 60% plain text, 30% structured JSON, 10% exception stack traces.
    /// Size: 64–2048 bytes, mean ~256.
    /// </summary>
    public static RealisticMixedData BuildRealisticMixed(int count, int seed = 77777)
    {
        var random = new Random(seed);
        var messages = new List<string>(count);
        var utf8Messages = new List<byte[]>(count);
        var levels = new List<LogLevel>(count);

        for (var i = 0; i < count; i++)
        {
            var level = MixedLevelDistribution[random.Next(MixedLevelDistribution.Length)];
            levels.Add(level);

            var contentRoll = random.Next(100);
            string message;

            if (contentRoll < 60)
            {
                var size = 64 + random.Next(384);
                message = BuildPlainMessage(i, level, size, random);
            }
            else if (contentRoll < 90)
            {
                var size = 128 + random.Next(512);
                message = BuildStructuredJsonMessage(i, level, size, random);
            }
            else
            {
                message = BuildExceptionMessage(i, level, random);
            }

            messages.Add(message);
            utf8Messages.Add(Encoding.UTF8.GetBytes(message));
        }

        return new RealisticMixedData(messages, utf8Messages, levels);
    }

    private static string BuildPlainMessage(int index, LogLevel level, int targetSize, Random random)
    {
        var timestamp = BaseTimestampUtc.AddSeconds(index);
        var levelTag = FormatLevel(level);
        var baseText = $"[{timestamp:HH:mm:ss} {levelTag}] Request #{index} processed in {random.Next(1, 500)}ms from {RandomIp(random)}";

        if (Encoding.UTF8.GetByteCount(baseText) >= targetSize)
            return baseText;

        var sb = new StringBuilder(baseText, targetSize + 32);
        while (Encoding.UTF8.GetByteCount(sb.ToString()) < targetSize)
            sb.Append(" ctx=").Append(random.Next(10000));
        return sb.ToString();
    }

    private static string BuildStructuredJsonMessage(int index, LogLevel level, int targetSize, Random random)
    {
        var timestamp = BaseTimestampUtc.AddSeconds(index);
        var levelTag = FormatLevel(level);
        var payload = JsonSerializer.Serialize(new
        {
            id = index,
            source = "api-gateway",
            level = levelTag,
            elapsedMs = random.Next(1, 2000),
            orderId = $"ORD-{random.Next(10000, 99999)}",
            uri = $"https://api.example.com/v2/orders/{random.Next(1000)}",
            userId = $"usr_{random.Next(100, 999)}",
            success = random.Next(100) < 85,
            tags = new[] { "production", index % 2 == 0 ? "us-east" : "eu-west" }
        });

        var baseText = $"[{timestamp:HH:mm:ss} {levelTag}] {payload}";
        if (Encoding.UTF8.GetByteCount(baseText) >= targetSize)
            return baseText;

        var sb = new StringBuilder(baseText, targetSize + 32);
        while (Encoding.UTF8.GetByteCount(sb.ToString()) < targetSize)
            sb.Append(" extra=").Append(random.Next(10000));
        return sb.ToString();
    }

    private static string BuildExceptionMessage(int index, LogLevel level, Random random)
    {
        var timestamp = BaseTimestampUtc.AddSeconds(index);
        var levelTag = FormatLevel(level);
        return $"[{timestamp:HH:mm:ss} {levelTag}] Unhandled exception in request #{index} (attempt {random.Next(1, 5)})\n{SampleStackTrace}";
    }

    private static string FormatLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "INF"
    };

    private static string RandomIp(Random random) =>
        $"{random.Next(10, 200)}.{random.Next(0, 255)}.{random.Next(0, 255)}.{random.Next(1, 254)}";

    private static LogLevel[] BuildLevelDistribution()
    {
        var dist = new LogLevel[100];
        var idx = 0;
        Fill(dist, ref idx, LogLevel.Trace, 10);
        Fill(dist, ref idx, LogLevel.Debug, 20);
        Fill(dist, ref idx, LogLevel.Information, 40);
        Fill(dist, ref idx, LogLevel.Warning, 20);
        Fill(dist, ref idx, LogLevel.Error, 8);
        Fill(dist, ref idx, LogLevel.Critical, 2);
        return dist;
    }

    private static void Fill(LogLevel[] arr, ref int idx, LogLevel level, int count)
    {
        for (var i = 0; i < count && idx < arr.Length; i++, idx++)
            arr[idx] = level;
    }

    private static string BuildSingleMessage(
        int index,
        int sizeBytes,
        TokenDensity tokenDensity,
        bool structuredPayload,
        Random random)
    {
        var timestamp = BaseTimestampUtc.AddSeconds(index);
        var payload = structuredPayload
            ? JsonSerializer.Serialize(new
            {
                id = index,
                source = "bench",
                level = "INF",
                elapsedMs = (index % 200) + 1,
                token = "ORD-12345",
                uri = "https://example.com/revit/docs?x=1&y=2",
                guid = CreateDeterministicGuid(random).ToString("N")
            })
            : "simple-message";

        var tokenPart = tokenDensity switch
        {
            TokenDensity.None => "plain",
            TokenDensity.Medium => "WRN threshold=1024 ORD-12345",
            _ => "WRN threshold=1024 ERR E500 ORD-12345 https://example.com/revit/docs IfcGuid=3H7dK2oQfA5Q9v7cYx2mQa"
        };

        var baseText = $"[{timestamp:HH:mm:ss} INF] {tokenPart} idx={index} payload={payload}";
        if (Encoding.UTF8.GetByteCount(baseText) >= sizeBytes)
            return baseText;

        var builder = new StringBuilder(baseText, sizeBytes + 32);
        while (Encoding.UTF8.GetByteCount(builder.ToString()) < sizeBytes)
            builder.Append(" filler-token");
        return builder.ToString();
    }

    private static Guid CreateDeterministicGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }
}

public sealed class RealisticMixedData
{
    public IReadOnlyList<string> Messages { get; }
    public IReadOnlyList<byte[]> Utf8Messages { get; }
    public IReadOnlyList<LogLevel> Levels { get; }

    public RealisticMixedData(
        IReadOnlyList<string> messages,
        IReadOnlyList<byte[]> utf8Messages,
        IReadOnlyList<LogLevel> levels)
    {
        Messages = messages;
        Utf8Messages = utf8Messages;
        Levels = levels;
    }
}
