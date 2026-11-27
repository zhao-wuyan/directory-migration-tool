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
        List<ChatMessage> materializedMessages = messages as List<ChatMessage> ?? messages.ToList();
        string? configuredEffort = TryExtractReasoningEffort(options);
        bool includeUsage = true;
        string? activeEffort = configuredEffort;
        HttpResponseMessage resp = await SendStreamingWithFallbackAsync(
            materializedMessages,
            options,
            includeUsage,
            activeEffort,
            cancellationToken).ConfigureAwait(false);

        using HttpResponseMessage activeResp = resp;
        string? mediaType = activeResp.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && mediaType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            string fullBody = await activeResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ChatResponse full = ParseNonStreamingResponse(fullBody);
            foreach (ChatResponseUpdate update in full.ToChatResponseUpdates())
            {
                yield return update;
            }
            yield break;
        }

        await using Stream stream = await activeResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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

            // 先解析根级 usage 与服务端 timings（OpenAI / DeepSeek / Qwen / Ollama / llama.cpp / Halogen Flash Server）
            UsageDetails? chunkUsage = TryParseUsageDetails(root);
            double? serverPredictedTps = TryParseServerPredictedTps(root);
            if (chunkUsage is not null || serverPredictedTps is not null)
            {
                var metaUpdate = chunkUsage is not null
                    ? new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(chunkUsage)])
                    : new ChatResponseUpdate { Role = ChatRole.Assistant };
                metaUpdate.ModelId = _modelName;
                if (serverPredictedTps is double exactTps)
                {
                    metaUpdate.AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["server_predicted_tps"] = exactTps
                    };
                }
                yield return metaUpdate;
            }

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
            StringBuilder? toolDeltaTextBuilder = null;
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
                            string? nameChunk = nameEl.GetString();
                            if (!string.IsNullOrEmpty(nameChunk))
                            {
                                acc.Name += nameChunk;
                                (toolDeltaTextBuilder ??= new StringBuilder()).Append(nameChunk);
                            }
                        }
                        if (fnEl.TryGetProperty("arguments", out JsonElement argsEl) && argsEl.ValueKind == JsonValueKind.String)
                        {
                            string? argsChunk = argsEl.GetString();
                            if (!string.IsNullOrEmpty(argsChunk))
                            {
                                acc.Arguments.Append(argsChunk);
                                (toolDeltaTextBuilder ??= new StringBuilder()).Append(argsChunk);
                            }
                        }
                    }
                }
            }

            if (contents.Count > 0)
            {
                var contentUpdate = new ChatResponseUpdate(ChatRole.Assistant, contents)
                {
                    ModelId = _modelName
                };
                if (toolDeltaTextBuilder is { Length: > 0 })
                {
                    contentUpdate.AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["tool_args_delta_text"] = toolDeltaTextBuilder.ToString()
                    };
                }
                yield return contentUpdate;
            }
            else if (hasToolCallDelta)
            {
                // 模型正在持续流式输出工具调用参数：发出保活更新并携带本分片文本，驱动上层动态空闲看门狗续期与实时 tok/s 计算
                var toolDeltaUpdate = new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    ModelId = _modelName
                };
                if (toolDeltaTextBuilder is { Length: > 0 })
                {
                    toolDeltaUpdate.AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["tool_args_delta_text"] = toolDeltaTextBuilder.ToString()
                    };
                }
                yield return toolDeltaUpdate;
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

            AiModelContextProbeResult? probe = await TryProbeContextBestEffortAsync(cancellationToken).ConfigureAwait(false);

            bool calledTool = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Any();
            string badge = calledTool ? "已测通 · 支持 Tool Calling" : "已测通 · 兼容 Tool 参数";
            string ctxPart = probe is { Found: true, ContextWindowTokens: int detectedCtx }
                ? $"，已自动识别上下文 {FormatCompactTokens(detectedCtx)}（{probe.SourceEndpoint}）"
                : string.Empty;
            string detail = calledTool
                ? $"连接成功（{sw.ElapsedMilliseconds} ms），模型 {_modelName} 已实际触发只读工具调用{ctxPart}。"
                : $"连接成功（{sw.ElapsedMilliseconds} ms），端点接受 tools 定义并正常返回模型回复{ctxPart}。";

            return new AiConnectionTestResult(
                Success: true,
                SupportsToolCalling: true,
                ElapsedMilliseconds: sw.ElapsedMilliseconds,
                StatusBadgeText: badge,
                DetailMessage: detail,
                DetectedContextWindowTokens: probe?.ContextWindowTokens,
                DetectedContextWindowSource: probe?.SourceEndpoint);
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
                AiModelContextProbeResult? probe = await TryProbeContextBestEffortAsync(cancellationToken).ConfigureAwait(false);
                return new AiConnectionTestResult(
                    Success: true,
                    SupportsToolCalling: false,
                    ElapsedMilliseconds: sw.ElapsedMilliseconds,
                    StatusBadgeText: "已测通 · 不支持 Tool Calling",
                    DetailMessage: "端点可连通，但当前模型不支持 OpenAI Tool Calling，建议切换支持函数调用的模型（如 deepseek-chat / qwen-plus / qwen2.5）。",
                    DetectedContextWindowTokens: probe?.ContextWindowTokens,
                    DetectedContextWindowSource: probe?.SourceEndpoint);
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

    /// <summary>
    /// 只读探测当前模型的真实上下文窗口大小（不消耗任何对话 Token）。
    /// 自动兼容 Halogen Flash Server (/props, /slots)、LM Studio (/api/v0/models, /api/v1/models)、
    /// Ollama (/api/ps, /api/show)、llama.cpp (/props, /slots) 以及 OpenAI 兼容网关 (/v1/models: vLLM / SGLang / OpenRouter / NewAPI 等)。
    /// </summary>
    public async Task<AiModelContextProbeResult> ProbeModelContextWindowAsync(CancellationToken cancellationToken = default)
    {
        Uri apiRoot = ResolveApiRootUri(_completionsUri);
        Uri serviceRoot = ResolveServiceRootUri(apiRoot);
        Uri origin = new(_completionsUri.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        bool isSelfHosted = IsLocalOrSelfHostedHost(_completionsUri);
        bool isLikelyHalogenOrLlama = _completionsUri.Port == 8731
            || _modelName.Contains("halogen", StringComparison.OrdinalIgnoreCase);

        bool triedPropsAndSlots = false;

        // 0. 若端口为 Halogen 默认端口(8731)或模型名含 halogen，优先直查 /props 与 /slots（一步命中，免去 LM Studio/Ollama 404）
        if (isLikelyHalogenOrLlama)
        {
            triedPropsAndSlots = true;
            AiModelContextProbeResult? halogenRes = await TryProbePropsAndSlotsAsync(serviceRoot, origin, cancellationToken).ConfigureAwait(false);
            if (halogenRes is not null)
            {
                return halogenRes;
            }
        }

        // 1. 本地/局域网/Tailscale 自托管推理服务优先探测原生接口（可拿到实际加载的 KV Cache / HALOGEN_CTX / num_ctx / n_ctx）
        if (isSelfHosted)
        {
            if (!triedPropsAndSlots)
            {
                triedPropsAndSlots = true;
                AiModelContextProbeResult? propsRes = await TryProbePropsAndSlotsAsync(serviceRoot, origin, cancellationToken).ConfigureAwait(false);
                if (propsRes is not null)
                {
                    return propsRes;
                }
            }

            string? lmV0Json = await TryGetStringAsync(BuildRelativeUri(serviceRoot, "/api/v0/models"), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(lmV0Json)
                && TryExtractContextWindowFromModelsJson(lmV0Json, _modelName) is int lmV0Ctx)
            {
                return new AiModelContextProbeResult(true, lmV0Ctx, "/api/v0/models",
                    $"已从 LM Studio (/api/v0/models) 获取上下文上限：{FormatCompactTokens(lmV0Ctx)} tokens。");
            }

            string? lmV1Json = await TryGetStringAsync(BuildRelativeUri(serviceRoot, "/api/v1/models"), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(lmV1Json)
                && TryExtractContextWindowFromModelsJson(lmV1Json, _modelName) is int lmV1Ctx)
            {
                return new AiModelContextProbeResult(true, lmV1Ctx, "/api/v1/models",
                    $"已从 LM Studio (/api/v1/models) 获取上下文上限：{FormatCompactTokens(lmV1Ctx)} tokens。");
            }

            string? ollamaPsJson = await TryGetStringAsync(BuildRelativeUri(serviceRoot, "/api/ps"), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ollamaPsJson)
                && TryExtractContextWindowFromModelsJson(ollamaPsJson, _modelName) is int ollamaPsCtx)
            {
                return new AiModelContextProbeResult(true, ollamaPsCtx, "/api/ps",
                    $"已从 Ollama 运行态 (/api/ps) 获取当前上下文：{FormatCompactTokens(ollamaPsCtx)} tokens。");
            }

            string ollamaShowBody = JsonSerializer.Serialize(new { model = _modelName });
            string? ollamaShowJson = await TryPostStringAsync(BuildRelativeUri(serviceRoot, "/api/show"), ollamaShowBody, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ollamaShowJson)
                && TryExtractContextWindowFromOllamaShowJson(ollamaShowJson) is int ollamaShowCtx)
            {
                return new AiModelContextProbeResult(true, ollamaShowCtx, "/api/show",
                    $"已从 Ollama (/api/show) 获取模型上下文上限：{FormatCompactTokens(ollamaShowCtx)} tokens。");
            }
        }

        // 2. 通用 OpenAI 兼容 /models 与 /models/{id}（支持 vLLM / SGLang / OpenRouter / Together / NewAPI / LiteLLM 等）
        var modelsListUri = new Uri(apiRoot.ToString().TrimEnd('/') + "/models", UriKind.Absolute);
        string? modelsJson = await TryGetStringAsync(modelsListUri, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(modelsJson)
            && TryExtractContextWindowFromModelsJson(modelsJson, _modelName) is int listCtx)
        {
            return new AiModelContextProbeResult(true, listCtx, "/models",
                $"已从 API (/models) 获取模型上下文上限：{FormatCompactTokens(listCtx)} tokens。");
        }

        var singleModelUri = new Uri(apiRoot.ToString().TrimEnd('/') + "/models/" + Uri.EscapeDataString(_modelName), UriKind.Absolute);
        string? singleJson = await TryGetStringAsync(singleModelUri, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(singleJson)
            && TryExtractContextWindowFromModelsJson(singleJson, _modelName) is int singleCtx)
        {
            return new AiModelContextProbeResult(true, singleCtx, "/models/{id}",
                $"已从 API (/models/{{id}}) 获取模型上下文上限：{FormatCompactTokens(singleCtx)} tokens。");
        }

        // 3. 公网反代场景兜底：若前面未探过 /props 与 /slots（例如 HTTPS 反代自建的 Halogen / llama.cpp），再补探一次
        if (!triedPropsAndSlots)
        {
            AiModelContextProbeResult? proxyPropsRes = await TryProbePropsAndSlotsAsync(serviceRoot, origin, cancellationToken).ConfigureAwait(false);
            if (proxyPropsRes is not null)
            {
                return proxyPropsRes;
            }
        }

        return new AiModelContextProbeResult(
            Found: false,
            ContextWindowTokens: null,
            SourceEndpoint: null,
            DetailMessage: $"该端点未在模型元数据接口返回 {_modelName} 的上下文大小，已回退型号估算；你也可以手动指定上下文上限。");
    }

    private async Task<AiModelContextProbeResult?> TryProbePropsAndSlotsAsync(Uri serviceRoot, Uri origin, CancellationToken cancellationToken)
    {
        Uri propsUri = BuildRelativeUri(serviceRoot, "/props");
        string? propsJson = await TryGetStringAsync(propsUri, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(propsJson)
            && TryExtractContextWindowFromLlamaPropsJson(propsJson) is int propsCtx)
        {
            return new AiModelContextProbeResult(true, propsCtx, "/props",
                $"已从服务端 (/props) 获取当前上下文上限：{FormatCompactTokens(propsCtx)} tokens。");
        }

        Uri slotsUri = BuildRelativeUri(serviceRoot, "/slots");
        string? slotsJson = await TryGetStringAsync(slotsUri, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(slotsJson)
            && TryExtractContextWindowFromSlotsJson(slotsJson) is int slotsCtx)
        {
            return new AiModelContextProbeResult(true, slotsCtx, "/slots",
                $"已从服务端 (/slots) 获取当前上下文上限：{FormatCompactTokens(slotsCtx)} tokens。");
        }

        // 若配置了带路径前缀的反代（如 /halogen/v1）且前缀下未挂载 /props，再尝试根路径 /props
        if (!Uri.Equals(serviceRoot, origin))
        {
            string? rootPropsJson = await TryGetStringAsync(new Uri(origin, "/props"), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(rootPropsJson)
                && TryExtractContextWindowFromLlamaPropsJson(rootPropsJson) is int rootPropsCtx)
            {
                return new AiModelContextProbeResult(true, rootPropsCtx, "/props",
                    $"已从服务端 (/props) 获取当前上下文上限：{FormatCompactTokens(rootPropsCtx)} tokens。");
            }
        }

        return null;
    }

    private static Uri ResolveServiceRootUri(Uri apiRoot)
    {
        string raw = apiRoot.ToString().TrimEnd('/');
        if (raw.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[..^"/v1".Length];
        }
        return new Uri(raw.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static Uri BuildRelativeUri(Uri serviceRoot, string relativePath)
    {
        string rootStr = serviceRoot.ToString().TrimEnd('/');
        string rel = relativePath.StartsWith('/') ? relativePath : "/" + relativePath;
        return new Uri(rootStr + rel, UriKind.Absolute);
    }

    private async Task<AiModelContextProbeResult?> TryProbeContextBestEffortAsync(CancellationToken outerCt)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            return await ProbeModelContextWindowAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static Uri ResolveApiRootUri(Uri completionsUri)
    {
        string raw = completionsUri.ToString().TrimEnd('/');
        if (raw.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[..^"/chat/completions".Length];
        }
        return new Uri(raw.TrimEnd('/'), UriKind.Absolute);
    }

    private static bool IsLocalOrSelfHostedHost(Uri uri)
    {
        if (uri.IsLoopback
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (System.Net.IPAddress.TryParse(uri.Host, out System.Net.IPAddress? ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            // RFC1918 内网 + Tailscale / CGNAT (100.64.0.0/10)
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        // 无点局域网主机名（如 strix-halo）、mDNS (.local) 或内网域名后缀
        string host = uri.Host;
        if (!host.Contains('.')
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private async Task<string?> TryGetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            reqCts.CancelAfter(TimeSpan.FromSeconds(2.5));
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, reqCts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(reqCts.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryPostStringAsync(Uri uri, string jsonBody, CancellationToken cancellationToken)
    {
        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            reqCts.CancelAfter(TimeSpan.FromSeconds(2.5));
            using var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, reqCts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(reqCts.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    internal static int? TryExtractContextWindowFromModelsJson(string json, string targetModelName)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string normalizedTarget = NormalizeModelId(targetModelName);

            if (root.ValueKind == JsonValueKind.Object)
            {
                // 单模型对象直接返回（如 GET /v1/models/{id}）
                if (!root.TryGetProperty("data", out JsonElement dataArr)
                    && !root.TryGetProperty("models", out JsonElement modelsArr))
                {
                    return ExtractContextFromModelObject(root);
                }

                JsonElement list = root.TryGetProperty("data", out JsonElement d) && d.ValueKind == JsonValueKind.Array
                    ? d
                    : (root.TryGetProperty("models", out JsonElement m) && m.ValueKind == JsonValueKind.Array ? m : default);

                if (list.ValueKind == JsonValueKind.Array)
                {
                    return ExtractFromModelArray(list, targetModelName, normalizedTarget);
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                return ExtractFromModelArray(root, targetModelName, normalizedTarget);
            }
        }
        catch
        {
        }
        return null;
    }

    private static int? ExtractFromModelArray(JsonElement array, string rawTarget, string normalizedTarget)
    {
        int? exactMatch = null;
        int? fuzzyMatch = null;
        int arrayCount = 0;
        int? singleElementFallback = null;

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            arrayCount++;

            int? ctx = ExtractContextFromModelObject(item);
            if (ctx is null) continue;

            if (arrayCount == 1)
            {
                singleElementFallback = ctx;
            }
            else
            {
                singleElementFallback = null;
            }

            string[] candidateIds = GetModelIdentifiers(item);
            foreach (string id in candidateIds)
            {
                if (string.Equals(id, rawTarget, StringComparison.OrdinalIgnoreCase))
                {
                    exactMatch = ctx;
                    break;
                }
                if (fuzzyMatch is null && string.Equals(NormalizeModelId(id), normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    fuzzyMatch = ctx;
                }
            }

            if (exactMatch.HasValue) break;
        }

        return exactMatch ?? fuzzyMatch ?? singleElementFallback;
    }

    private static string[] GetModelIdentifiers(JsonElement item)
    {
        var ids = new List<string>(3);
        foreach (string key in new[] { "id", "model", "name", "key" })
        {
            if (item.TryGetProperty(key, out JsonElement el) && el.ValueKind == JsonValueKind.String)
            {
                string? val = el.GetString();
                if (!string.IsNullOrWhiteSpace(val)) ids.Add(val.Trim());
            }
        }
        return ids.ToArray();
    }

    private static string NormalizeModelId(string? id)
    {
        string s = (id ?? string.Empty).Trim().ToLowerInvariant();
        if (s.EndsWith(":latest", StringComparison.Ordinal))
        {
            s = s[..^":latest".Length];
        }
        return s;
    }

    private static int? ExtractContextFromModelObject(JsonElement obj)
    {
        // LM Studio /api/v1/models：优先取已加载实例的实际配置 context_length
        if (obj.TryGetProperty("loaded_instances", out JsonElement instances) && instances.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement inst in instances.EnumerateArray())
            {
                if (inst.ValueKind == JsonValueKind.Object
                    && inst.TryGetProperty("config", out JsonElement cfg)
                    && cfg.ValueKind == JsonValueKind.Object
                    && TryReadValidContextInt(cfg, "context_length") is int loadedInstCtx)
                {
                    return loadedInstCtx;
                }
            }
        }

        // 常见顶层字段按「实载优先 → 上限次之」排序
        foreach (string field in new[]
        {
            "loaded_context_length",
            "context_length",
            "max_model_len",
            "max_context_length",
            "context_window",
            "input_token_limit",
            "max_input_tokens",
            "n_ctx"
        })
        {
            if (TryReadValidContextInt(obj, field) is int v)
            {
                return v;
            }
        }

        // 常见嵌套字段：OpenRouter top_provider.context_length / llama.cpp meta.n_ctx_train / limits.context / capabilities.limits
        if (obj.TryGetProperty("top_provider", out JsonElement tp) && tp.ValueKind == JsonValueKind.Object
            && TryReadValidContextInt(tp, "context_length") is int tpCtx)
        {
            return tpCtx;
        }
        if (obj.TryGetProperty("meta", out JsonElement meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (TryReadValidContextInt(meta, "n_ctx") is int metaCtx) return metaCtx;
            if (TryReadValidContextInt(meta, "n_ctx_train") is int metaTrainCtx) return metaTrainCtx;
        }
        if (obj.TryGetProperty("limits", out JsonElement limits) && limits.ValueKind == JsonValueKind.Object)
        {
            if (TryReadValidContextInt(limits, "context") is int limCtx) return limCtx;
            if (TryReadValidContextInt(limits, "max_context_window_tokens") is int limMaxCtx) return limMaxCtx;
        }
        if (obj.TryGetProperty("capabilities", out JsonElement caps) && caps.ValueKind == JsonValueKind.Object
            && caps.TryGetProperty("limits", out JsonElement capLimits) && capLimits.ValueKind == JsonValueKind.Object
            && TryReadValidContextInt(capLimits, "max_context_window_tokens") is int capCtx)
        {
            return capCtx;
        }

        return null;
    }

    internal static int? TryExtractContextWindowFromOllamaShowJson(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // 1. 若 Modelfile parameters 显式指定 num_ctx，优先采用
            if (root.TryGetProperty("parameters", out JsonElement paramsEl) && paramsEl.ValueKind == JsonValueKind.String)
            {
                string paramText = paramsEl.GetString() ?? string.Empty;
                foreach (string line in paramText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2
                        && parts[0].Equals("num_ctx", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(parts[1], out int parsedNumCtx)
                        && IsValidContextWindow(parsedNumCtx))
                    {
                        return parsedNumCtx;
                    }
                }
            }

            // 2. 否则从 model_info 查找 *.context_length（如 qwen3.context_length / llama.context_length）
            if (root.TryGetProperty("model_info", out JsonElement info) && info.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in info.EnumerateObject())
                {
                    if (prop.Name.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase)
                        && TryReadJsonInt(prop.Value) is int infoCtx
                        && IsValidContextWindow(infoCtx))
                    {
                        return infoCtx;
                    }
                }
            }
        }
        catch
        {
        }
        return null;
    }

    internal static int? TryExtractContextWindowFromLlamaPropsJson(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("default_generation_settings", out JsonElement dgs)
                && dgs.ValueKind == JsonValueKind.Object
                && TryReadValidContextInt(dgs, "n_ctx") is int nCtx)
            {
                return nCtx;
            }
            if (TryReadValidContextInt(root, "n_ctx") is int rootCtx)
            {
                return rootCtx;
            }
        }
        catch
        {
        }
        return null;
    }

    internal static int? TryExtractContextWindowFromSlotsJson(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement slot in root.EnumerateArray())
                {
                    if (slot.ValueKind == JsonValueKind.Object
                        && TryReadValidContextInt(slot, "n_ctx") is int slotCtx)
                    {
                        return slotCtx;
                    }
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static int? TryReadValidContextInt(JsonElement obj, string propName)
    {
        if (obj.TryGetProperty(propName, out JsonElement el)
            && TryReadJsonInt(el) is int parsed
            && IsValidContextWindow(parsed))
        {
            return parsed;
        }
        return null;
    }

    private static int? TryReadJsonInt(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long l) && l >= int.MinValue && l <= int.MaxValue)
        {
            return (int)l;
        }
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString()?.Trim(), out int s))
        {
            return s;
        }
        return null;
    }

    private static bool IsValidContextWindow(int value) =>
        value >= AiProviderSettings.MinContextWindowTokens && value <= AiProviderSettings.MaxContextWindowTokens;

    private static string FormatCompactTokens(int tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:0.##}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:0.#}K";
        return tokens.ToString();
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

    private async Task<HttpResponseMessage> SendStreamingWithFallbackAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        bool includeStreamUsage,
        string? reasoningEffort,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            string payload = BuildRequestPayload(messages, options, stream: true, includeStreamUsage, reasoningEffort);
            HttpResponseMessage resp;
            using (var req = CreateHttpRequest(payload))
            {
                resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (resp.IsSuccessStatusCode)
            {
                return resp;
            }

            var statusCode = resp.StatusCode;
            string errBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            resp.Dispose();

            if ((int)statusCode == 400)
            {
                if (includeStreamUsage && errBody.Contains("stream_options", StringComparison.OrdinalIgnoreCase))
                {
                    includeStreamUsage = false;
                    continue;
                }

                if (!string.IsNullOrEmpty(reasoningEffort)
                    && (errBody.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase)
                        || errBody.Contains("reasoning", StringComparison.OrdinalIgnoreCase)))
                {
                    // 优先向标准档位平滑降级（max/xhigh -> high，minimal/none -> low），若仍不支持则省略参数重试
                    reasoningEffort = reasoningEffort switch
                    {
                        "max" or "xhigh" => "high",
                        "minimal" => "low",
                        _ => null
                    };
                    continue;
                }
            }

            throw new HttpRequestException(FormatSafeHttpError(statusCode, errBody));
        }

        throw new HttpRequestException("模型端点拒绝了请求参数。");
    }

    private static string? TryExtractReasoningEffort(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not null
            && options.AdditionalProperties.TryGetValue("reasoning_effort", out object? raw)
            && raw is string str
            && !string.IsNullOrWhiteSpace(str))
        {
            return str.Trim().ToLowerInvariant();
        }
        return null;
    }

    private string BuildRequestPayload(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        bool stream,
        bool includeStreamUsage = false,
        string? reasoningEffortOverride = null)
    {
        var root = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(options?.ModelId) ? _modelName : options!.ModelId,
            ["stream"] = stream
        };

        if (stream && includeStreamUsage)
        {
            root["stream_options"] = new JsonObject
            {
                ["include_usage"] = true
            };
        }

        string? effort = reasoningEffortOverride ?? TryExtractReasoningEffort(options);
        if (!string.IsNullOrEmpty(effort))
        {
            root["reasoning_effort"] = effort;
        }

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
        UsageDetails? usage = TryParseUsageDetails(root);
        if (usage is not null)
        {
            contents.Add(new UsageContent(usage));
        }

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
            FinishReason = finishReason,
            Usage = usage
        };
    }

    private static double? TryParseServerPredictedTps(JsonElement root)
    {
        if (!root.TryGetProperty("timings", out JsonElement timingsEl) || timingsEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (timingsEl.TryGetProperty("predicted_per_second", out JsonElement ppsEl)
            && ppsEl.ValueKind == JsonValueKind.Number
            && ppsEl.TryGetDouble(out double pps)
            && double.IsFinite(pps)
            && pps > 0
            && pps < 100_000)
        {
            return Math.Round(pps, 1);
        }

        if (timingsEl.TryGetProperty("predicted_n", out JsonElement pnEl)
            && pnEl.ValueKind == JsonValueKind.Number
            && pnEl.TryGetDouble(out double pn)
            && pn > 0
            && timingsEl.TryGetProperty("predicted_ms", out JsonElement pmsEl)
            && pmsEl.ValueKind == JsonValueKind.Number
            && pmsEl.TryGetDouble(out double pms)
            && pms > 0)
        {
            double computed = pn * 1000.0 / pms;
            if (double.IsFinite(computed) && computed > 0 && computed < 100_000)
            {
                return Math.Round(computed, 1);
            }
        }

        return null;
    }

    private static UsageDetails? TryParseUsageDetails(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usageEl) || usageEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        long? promptTokens = TryReadLong(usageEl, "prompt_tokens") ?? TryReadLong(usageEl, "input_tokens");
        long? completionTokens = TryReadLong(usageEl, "completion_tokens") ?? TryReadLong(usageEl, "output_tokens");
        long? totalTokens = TryReadLong(usageEl, "total_tokens");

        if (promptTokens is null && completionTokens is null && totalTokens is null)
        {
            return null;
        }

        return new UsageDetails
        {
            InputTokenCount = promptTokens,
            OutputTokenCount = completionTokens,
            TotalTokenCount = totalTokens ?? ((promptTokens ?? 0) + (completionTokens ?? 0))
        };
    }

    private static long? TryReadLong(JsonElement obj, string propName)
    {
        if (obj.TryGetProperty(propName, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long val) && val >= 0)
        {
            return val;
        }
        return null;
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
