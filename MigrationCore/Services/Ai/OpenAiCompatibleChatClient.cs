using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using MigrationCore.Models.Ai;

namespace MigrationCore.Services.Ai;

/// <summary>
/// 轻量级 OpenAI 兼容协议 IChatClient 实现（纯 HttpClient + System.Text.Json，实现 Microsoft.Extensions.AI 抽象）。
/// 兼容 DeepSeek、通义千问、OpenAI、本地 Ollama、LM Studio 及任意 OpenAI Chat Completions 端点，
/// 支持 Tool Calling、流式 SSE 与 reasoning_content 解析，避免引入大型厂商 SDK 膨胀 Lite 包体积。
/// </summary>
public sealed class OpenAiCompatibleChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _completionsUri;
    private readonly string _modelName;
    private readonly string? _apiKey;
    private readonly ChatClientMetadata _metadata;

    public OpenAiCompatibleChatClient(
        string endpoint,
        string modelName,
        string? apiKey = null,
        TimeSpan? timeout = null,
        HttpClient? httpClient = null)
    {
        _completionsUri = ResolveChatCompletionsUri(endpoint);
        _modelName = string.IsNullOrWhiteSpace(modelName)
            ? throw new ArgumentException("模型名称不能为空。", nameof(modelName))
            : modelName.Trim();
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            // 流式多轮分析由上层 CancellationToken 统一管控超时，避免 HttpClient.Timeout 与外层超时竞争
            _httpClient = new HttpClient
            {
                Timeout = timeout ?? Timeout.InfiniteTimeSpan
            };
            _ownsHttpClient = true;
        }

        _metadata = new ChatClientMetadata("OpenAiCompatible", _completionsUri, _modelName);
    }

    public static Uri ResolveChatCompletionsUri(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("API Endpoint 不能为空。", nameof(endpoint));
        }

        string trimmed = endpoint.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("API Endpoint 必须是有效的 http:// 或 https:// 地址。", nameof(endpoint));
        }

        string normalized = trimmed.TrimEnd('/');
        if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(normalized, UriKind.Absolute);
        }
        return new Uri(normalized + "/chat/completions", UriKind.Absolute);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string payload = BuildRequestPayload(messages, options, stream: false);
        using var req = CreateHttpRequest(payload);
        using HttpResponseMessage resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(FormatSafeHttpError(resp.StatusCode, body));
        }

        return ParseNonStreamingResponse(body);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string payload = BuildRequestPayload(messages, options, stream: true);
        using var req = CreateHttpRequest(payload);
        using HttpResponseMessage resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            string errBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(FormatSafeHttpError(resp.StatusCode, errBody));
        }

        string? mediaType = resp.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && mediaType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            // 部分本地推理网关忽略 stream=true 直接返回完整 JSON
            string fullBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ChatResponse full = ParseNonStreamingResponse(fullBody);
            foreach (ChatResponseUpdate update in full.ToChatResponseUpdates())
            {
                yield return update;
            }
            yield break;
        }

        await using Stream stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var toolCallBuffers = new Dictionary<int, ToolCallAccumulator>();
        ChatFinishReason? finishReason = null;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            string data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) break;

            using JsonDocument doc = JsonDocument.Parse(data);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                continue;
            }

            JsonElement choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out JsonElement frEl) && frEl.ValueKind == JsonValueKind.String)
            {
                finishReason = ParseFinishReason(frEl.GetString());
            }

            if (!choice.TryGetProperty("delta", out JsonElement delta) || delta.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var contents = new List<AIContent>();

            if (delta.TryGetProperty("reasoning_content", out JsonElement reasonEl) && reasonEl.ValueKind == JsonValueKind.String)
            {
                string? reasoning = reasonEl.GetString();
                if (!string.IsNullOrEmpty(reasoning))
                {
                    contents.Add(new TextReasoningContent(reasoning));
                }
            }

            if (delta.TryGetProperty("content", out JsonElement contentEl) && contentEl.ValueKind == JsonValueKind.String)
            {
                string? text = contentEl.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    contents.Add(new TextContent(text));
                }
            }

            bool hasToolCallDelta = false;
            if (delta.TryGetProperty("tool_calls", out JsonElement tcArray) && tcArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement tc in tcArray.EnumerateArray())
                {
                    hasToolCallDelta = true;
                    int idx = tc.TryGetProperty("index", out JsonElement idxEl) && idxEl.TryGetInt32(out int parsedIdx)
                        ? parsedIdx
                        : toolCallBuffers.Count;

                    if (!toolCallBuffers.TryGetValue(idx, out ToolCallAccumulator? acc))
                    {
                        acc = new ToolCallAccumulator();
                        toolCallBuffers[idx] = acc;
                    }

                    if (tc.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(idEl.GetString()))
                    {
                        acc.CallId = idEl.GetString()!;
                    }
                    if (tc.TryGetProperty("function", out JsonElement fnEl) && fnEl.ValueKind == JsonValueKind.Object)
                    {
                        if (fnEl.TryGetProperty("name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        {
                            acc.Name += nameEl.GetString();
                        }
                        if (fnEl.TryGetProperty("arguments", out JsonElement argsEl) && argsEl.ValueKind == JsonValueKind.String)
                        {
                            acc.Arguments.Append(argsEl.GetString());
                        }
                    }
                }
            }

            if (contents.Count > 0)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, contents)
                {
                    ModelId = _modelName
                };
            }
            else if (hasToolCallDelta)
            {
                // 模型正在持续流式输出工具调用参数：发出无负载保活更新，驱动上层动态空闲看门狗续期
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    ModelId = _modelName
                };
            }
        }

        if (toolCallBuffers.Count > 0)
        {
            var toolContents = new List<AIContent>(toolCallBuffers.Count);
            foreach (var kv in toolCallBuffers.OrderBy(k => k.Key))
            {
                ToolCallAccumulator acc = kv.Value;
                string callId = string.IsNullOrWhiteSpace(acc.CallId) ? $"call_{Guid.NewGuid():N}" : acc.CallId;
                IDictionary<string, object?> argsDict = ParseArgumentsDictionary(acc.Arguments.ToString());
                toolContents.Add(new FunctionCallContent(callId, acc.Name, argsDict));
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, toolContents)
            {
                ModelId = _modelName,
                FinishReason = finishReason ?? ChatFinishReason.ToolCalls
            };
        }
        else if (finishReason is not null)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                ModelId = _modelName,
                FinishReason = finishReason
            };
        }
    }

    /// <summary>
    /// 校验端点连通性、模型有效性以及是否支持只读 Tool Calling（用于 BYOK 配置页「测试连接」按钮）。
    /// </summary>
    public async Task<AiConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            AIFunction pingTool = AIFunctionFactory.Create(
                () => "pong",
                name: "ping_readonly",
                description: "只读连通性测试工具，仅返回 pong");

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You are a connectivity test assistant. Reply briefly or invoke ping_readonly."),
                new(ChatRole.User, "Please reply OK or call ping_readonly once.")
            };

            var options = new ChatOptions
            {
                ModelId = _modelName,
                MaxOutputTokens = 64,
                Temperature = 0f,
                Tools = [pingTool]
            };

            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            bool calledTool = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Any();
            string badge = calledTool ? "已测通 · 支持 Tool Calling" : "已测通 · 兼容 Tool 参数";
            string detail = calledTool
                ? $"连接成功（{sw.ElapsedMilliseconds} ms），模型 {_modelName} 已实际触发只读工具调用。"
                : $"连接成功（{sw.ElapsedMilliseconds} ms），端点接受 tools 定义并正常返回模型回复。";

            return new AiConnectionTestResult(
                Success: true,
                SupportsToolCalling: true,
                ElapsedMilliseconds: sw.ElapsedMilliseconds,
                StatusBadgeText: badge,
                DetailMessage: detail);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("tool", StringComparison.OrdinalIgnoreCase))
        {
            // 若部分本地模型不支持 tools 参数，降级探活纯对话以便给出清晰提示
            try
            {
                var fallbackMessages = new List<ChatMessage>
                {
                    new(ChatRole.User, "Reply OK")
                };
                await GetResponseAsync(fallbackMessages, new ChatOptions { ModelId = _modelName, MaxOutputTokens = 16 }, cancellationToken)
                    .ConfigureAwait(false);
                sw.Stop();
                return new AiConnectionTestResult(
                    Success: true,
                    SupportsToolCalling: false,
                    ElapsedMilliseconds: sw.ElapsedMilliseconds,
                    StatusBadgeText: "已测通 · 不支持 Tool Calling",
                    DetailMessage: "端点可连通，但当前模型不支持 OpenAI Tool Calling，建议切换支持函数调用的模型（如 deepseek-chat / qwen-plus / qwen2.5）。");
            }
            catch (Exception fallbackEx)
            {
                sw.Stop();
                string safeErr = AiPathSanitizer.RedactSecret(fallbackEx.Message, _apiKey);
                return new AiConnectionTestResult(false, false, sw.ElapsedMilliseconds, "连接失败", safeErr);
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new AiConnectionTestResult(false, false, sw.ElapsedMilliseconds, "测试已取消", "连接测试已取消或超时。");
        }
        catch (Exception ex)
        {
            sw.Stop();
            string safeErr = AiPathSanitizer.RedactSecret(ex.Message, _apiKey);
            return new AiConnectionTestResult(false, false, sw.ElapsedMilliseconds, "连接失败", safeErr);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && serviceType == typeof(ChatClientMetadata))
        {
            return _metadata;
        }
        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }
        return null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private HttpRequestMessage CreateHttpRequest(string jsonPayload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _completionsUri)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return req;
    }

    private string BuildRequestPayload(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var root = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(options?.ModelId) ? _modelName : options!.ModelId,
            ["stream"] = stream
        };

        if (options?.Temperature is float temp)
        {
            root["temperature"] = temp;
        }
        if (options?.MaxOutputTokens is int maxTokens)
        {
            root["max_tokens"] = maxTokens;
        }

        var msgArray = new JsonArray();
        foreach (ChatMessage msg in messages)
        {
            if (msg.Role == ChatRole.Tool)
            {
                foreach (FunctionResultContent toolResult in msg.Contents.OfType<FunctionResultContent>())
                {
                    string resultStr = toolResult.Result switch
                    {
                        null => "null",
                        string s => s,
                        JsonElement je => je.GetRawText(),
                        _ => JsonSerializer.Serialize(toolResult.Result)
                    };
                    msgArray.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = toolResult.CallId,
                        ["content"] = resultStr
                    });
                }
                continue;
            }

            string roleStr = msg.Role == ChatRole.System ? "system"
                : msg.Role == ChatRole.User ? "user"
                : "assistant";

            var obj = new JsonObject
            {
                ["role"] = roleStr
            };

            string text = string.Concat(msg.Contents.OfType<TextContent>().Select(t => t.Text));
            var fnCalls = msg.Contents.OfType<FunctionCallContent>().ToList();

            if (fnCalls.Count > 0)
            {
                obj["content"] = string.IsNullOrEmpty(text) ? null : text;
                var tcArray = new JsonArray();
                foreach (FunctionCallContent call in fnCalls)
                {
                    string argsJson = call.Arguments is not null
                        ? JsonSerializer.Serialize(call.Arguments)
                        : "{}";
                    tcArray.Add(new JsonObject
                    {
                        ["id"] = call.CallId,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = argsJson
                        }
                    });
                }
                obj["tool_calls"] = tcArray;
            }
            else
            {
                obj["content"] = text;
            }

            msgArray.Add(obj);
        }
        root["messages"] = msgArray;

        if (options?.Tools is { Count: > 0 } tools)
        {
            var toolsArray = new JsonArray();
            foreach (AITool tool in tools)
            {
                if (tool is AIFunctionDeclaration fnDecl)
                {
                    JsonNode? schemaNode = JsonNode.Parse(fnDecl.JsonSchema.GetRawText());
                    toolsArray.Add(new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = fnDecl.Name,
                            ["description"] = fnDecl.Description ?? string.Empty,
                            ["parameters"] = schemaNode ?? new JsonObject { ["type"] = "object" }
                        }
                    });
                }
            }

            if (toolsArray.Count > 0)
            {
                root["tools"] = toolsArray;
                root["tool_choice"] = "auto";
            }
        }

        return root.ToJsonString();
    }

    private ChatResponse ParseNonStreamingResponse(string body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        var contents = new List<AIContent>();
        ChatFinishReason? finishReason = null;

        if (root.TryGetProperty("choices", out JsonElement choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            JsonElement choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out JsonElement frEl) && frEl.ValueKind == JsonValueKind.String)
            {
                finishReason = ParseFinishReason(frEl.GetString());
            }

            if (choice.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.Object)
            {
                if (message.TryGetProperty("reasoning_content", out JsonElement reasonEl) && reasonEl.ValueKind == JsonValueKind.String)
                {
                    string? r = reasonEl.GetString();
                    if (!string.IsNullOrEmpty(r))
                    {
                        contents.Add(new TextReasoningContent(r));
                    }
                }

                if (message.TryGetProperty("content", out JsonElement contentEl) && contentEl.ValueKind == JsonValueKind.String)
                {
                    string? text = contentEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        contents.Add(new TextContent(text));
                    }
                }

                if (message.TryGetProperty("tool_calls", out JsonElement tcArray) && tcArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement tc in tcArray.EnumerateArray())
                    {
                        string callId = tc.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                            ? (idEl.GetString() ?? $"call_{Guid.NewGuid():N}")
                            : $"call_{Guid.NewGuid():N}";

                        if (tc.TryGetProperty("function", out JsonElement fnEl) && fnEl.ValueKind == JsonValueKind.Object)
                        {
                            string name = fnEl.TryGetProperty("name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String
                                ? (nameEl.GetString() ?? string.Empty)
                                : string.Empty;
                            string rawArgs = fnEl.TryGetProperty("arguments", out JsonElement argsEl) && argsEl.ValueKind == JsonValueKind.String
                                ? (argsEl.GetString() ?? "{}")
                                : "{}";

                            contents.Add(new FunctionCallContent(callId, name, ParseArgumentsDictionary(rawArgs)));
                        }
                    }
                }
            }
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = _modelName,
            FinishReason = finishReason
        };
    }

    private static IDictionary<string, object?> ParseArgumentsDictionary(string rawJson)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rawJson)) return dict;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.Clone();
                }
            }
        }
        catch
        {
            // 模型返回非标准 JSON 时留空参数，交由下游工具显式报错反馈给模型
        }

        return dict;
    }

    private static ChatFinishReason? ParseFinishReason(string? raw) => raw switch
    {
        "stop" => ChatFinishReason.Stop,
        "length" => ChatFinishReason.Length,
        "tool_calls" or "function_call" => ChatFinishReason.ToolCalls,
        "content_filter" => ChatFinishReason.ContentFilter,
        _ => null
    };

    private string FormatSafeHttpError(System.Net.HttpStatusCode statusCode, string responseBody)
    {
        string snippet = AiPathSanitizer.SanitizeUntrustedText(
            AiPathSanitizer.RedactSecret(responseBody, _apiKey),
            maxLength: 320,
            preserveNewLines: false);

        return $"模型服务返回 HTTP {(int)statusCode} ({statusCode})：{(string.IsNullOrWhiteSpace(snippet) ? "无详细错误信息" : snippet)}";
    }

    private sealed class ToolCallAccumulator
    {
        public string CallId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }
}
