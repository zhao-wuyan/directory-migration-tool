using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models.Ai;
using MigrationCore.Models.Cleanup;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services;
using MigrationCore.Services.Ai;
using MoveWithSymlinkWPF.Helpers;

namespace MoveWithSymlinkWPF.ViewModels;

public partial class AiAssistantViewModel
{
    [RelayCommand]
    private void SelectPreset(AiPresetChipItem? chip)
    {
        if (chip is null) return;
        SelectedPreset = chip.Preset;
        foreach (AiPresetChipItem item in PresetChips)
        {
            item.IsSelected = item.Preset == chip.Preset;
        }

        if (chip.Preset != AiProviderPreset.Custom)
        {
            Endpoint = chip.Definition.DefaultEndpoint;
            ModelName = chip.Definition.DefaultModel;
        }
        ConfigFeedbackMessage = chip.HintText;
    }

    [RelayCommand]
    private void SelectResponseLanguage(string? languageKey)
    {
        ResponseLanguage = string.Equals(languageKey, "English", StringComparison.OrdinalIgnoreCase)
            ? AiResponseLanguage.English
            : AiResponseLanguage.Chinese;
    }

    [RelayCommand]
    private void SelectReasoningEffort(string? effortKey)
    {
        AiReasoningEffort parsed = Enum.TryParse(effortKey?.Trim(), ignoreCase: true, out AiReasoningEffort val)
            ? val
            : AiReasoningEffort.Default;
        ReasoningEffort = parsed;
        try
        {
            _savedSettings = _configStore.Save(_savedSettings with { ReasoningEffort = parsed }, plainApiKey: null);
        }
        catch
        {
        }
    }

    [RelayCommand]
    private void SelectContextWindowPreset(string? presetKey)
    {
        string normalized = (presetKey ?? string.Empty).Trim().ToUpperInvariant();
        int target = normalized switch
        {
            "" or "0" or "AUTO" => 0,
            "32K" => 32_768,
            "64K" => 65_536,
            "128K" => 131_072,
            "256K" => 262_144,
            "1M" => 1_048_576,
            _ => int.TryParse(normalized, out int custom) && custom > 0
                ? Math.Clamp(custom, AiProviderSettings.MinContextWindowTokens, AiProviderSettings.MaxContextWindowTokens)
                : 0
        };

        ContextWindowTokens = target;
        ContextProbeStatusMessage = target == 0
            ? $"已切换为自动模式：{ContextWindowSummaryText}"
            : $"已设为手动上限：{AiChatSessionViewModel.FormatTokenCount(target)} tokens";
        try
        {
            _savedSettings = _configStore.Save(_savedSettings with { ContextWindowTokens = target }, plainApiKey: null);
        }
        catch
        {
        }
        NotifyContextWindowMetadataChanged();
    }

    [RelayCommand]
    private void SelectOverallTimeoutPreset(string? minutesKey)
    {
        int parsed = int.TryParse(minutesKey?.Trim(), out int val) && val > 0
            ? Math.Clamp(val, AiProviderSettings.MinOverallTimeoutMinutes, AiProviderSettings.MaxOverallTimeoutMinutes)
            : AiProviderSettings.DefaultOverallTimeoutMinutes;

        OverallTimeoutMinutes = parsed;
        int overallSeconds = Math.Max(parsed * 60, Math.Clamp(TimeoutSeconds, 15, AiProviderSettings.HardMaxTimeoutSeconds));
        try
        {
            _savedSettings = _configStore.Save(_savedSettings with { OverallTimeoutSeconds = overallSeconds }, plainApiKey: null);
        }
        catch
        {
        }
        ConfigFeedbackMessage = $"已将单次分析总耗时安全上限设为 {parsed} 分钟。";
    }

    [RelayCommand]
    private async Task ProbeContextWindowAsync()
    {
        if (IsProbingContextWindow) return;

        string? validationError = ValidateDraftConfig(out AiProviderSettings draft, out string? plainKey);
        if (validationError is not null)
        {
            ContextProbeStatusMessage = validationError;
            ConfigFeedbackMessage = validationError;
            return;
        }

        if (!EnsureCloudPrivacyAcknowledged(draft, out bool acknowledgedNow))
        {
            return;
        }
        if (acknowledgedNow)
        {
            draft = draft with { CloudPrivacyAcknowledged = true };
        }

        IsProbingContextWindow = true;
        ContextProbeStatusMessage = $"正在从 API 获取 {draft.ModelName} 上下文大小…";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new OpenAiCompatibleChatClient(draft.Endpoint, draft.ModelName, plainKey, TimeSpan.FromSeconds(10));
            AiModelContextProbeResult probe = await client.ProbeModelContextWindowAsync(cts.Token);

            if (probe is { Found: true, ContextWindowTokens: int detectedTokens })
            {
                string targetKey = draft.BuildModelTargetKey();
                BuiltInAiAgentService.RememberProbedContextWindow(targetKey, detectedTokens, probe.SourceEndpoint);

                _savedSettings = _configStore.Save(
                    _savedSettings with
                    {
                        CloudPrivacyAcknowledged = draft.CloudPrivacyAcknowledged,
                        ContextWindowTokens = 0,
                        DetectedContextWindowTokens = detectedTokens,
                        DetectedContextWindowSource = probe.SourceEndpoint,
                        DetectedContextWindowTargetKey = targetKey
                    },
                    plainApiKey: null);

                ContextWindowTokens = 0;
                NotifyContextWindowMetadataChanged();
                ContextProbeStatusMessage = probe.DetailMessage;
                ConfigFeedbackMessage = probe.DetailMessage;
            }
            else
            {
                ContextProbeStatusMessage = probe.DetailMessage;
                ConfigFeedbackMessage = probe.DetailMessage;
            }
        }
        catch (Exception ex)
        {
            string safeErr = AiPathSanitizer.RedactSecret(ex.Message, plainKey);
            ContextProbeStatusMessage = $"获取失败：{safeErr}";
            ConfigFeedbackMessage = ContextProbeStatusMessage;
        }
        finally
        {
            IsProbingContextWindow = false;
        }
    }

    [RelayCommand]
    private void DecreaseChatFontSize()
    {
        ApplyAndPersistChatFontSize(ChatFontSize - 1.0);
    }

    [RelayCommand]
    private void IncreaseChatFontSize()
    {
        ApplyAndPersistChatFontSize(ChatFontSize + 1.0);
    }

    [RelayCommand]
    private void SetChatFontSize(string? presetKey)
    {
        double target = presetKey?.Trim() switch
        {
            "Small" => 10.5,
            "Large" => 13.0,
            "ExtraLarge" => 14.5,
            _ => AiProviderSettings.DefaultChatFontSize
        };
        ApplyAndPersistChatFontSize(target);
    }

    private void ApplyAndPersistChatFontSize(double rawSize)
    {
        double clamped = Math.Clamp(
            Math.Round(rawSize * 2.0) / 2.0,
            AiProviderSettings.MinChatFontSize,
            AiProviderSettings.MaxChatFontSize);

        ChatFontSize = clamped;
        try
        {
            _savedSettings = _configStore.Save(_savedSettings with { ChatFontSize = clamped }, plainApiKey: null);
        }
        catch
        {
            // 仅持久化失败时仍保留当前会话字号生效
        }
    }

    [RelayCommand]
    private void ClearSavedApiKey()
    {
        _savedSettings = _configStore.Save(_savedSettings, plainApiKey: string.Empty);
        MaskedApiKeyPreview = string.Empty;
        ClearPendingApiKeyBoxAction?.Invoke();
        ConfigFeedbackMessage = "已清除本地 DPAPI 加密保存的 API Key。";
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsTestingConnection) return;

        string? validationError = ValidateDraftConfig(out AiProviderSettings draft, out string? plainKey);
        if (validationError is not null)
        {
            SetConnectionBadge("配置不完整", "#FEF2F2", "#FECACA", "#C42B1C");
            ConfigFeedbackMessage = validationError;
            return;
        }

        if (!EnsureCloudPrivacyAcknowledged(draft, out bool acknowledgedNow))
        {
            return;
        }
        if (acknowledgedNow)
        {
            draft = draft with { CloudPrivacyAcknowledged = true };
        }

        IsTestingConnection = true;
        SetConnectionBadge("正在测试连接…", "#EBF5FF", "#BFDBFE", "#0078D4");
        ConfigFeedbackMessage = $"正在探活 {draft.Endpoint} ({draft.ModelName})…";

        _connectionTestCts?.Cancel();
        _connectionTestCts?.Dispose();
        _connectionTestCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        try
        {
            using var client = new OpenAiCompatibleChatClient(draft.Endpoint, draft.ModelName, plainKey, TimeSpan.FromSeconds(25));
            AiConnectionTestResult result = await client.TestConnectionAsync(_connectionTestCts.Token);

            if (result.Success)
            {
                if (result.SupportsToolCalling)
                {
                    SetConnectionBadge(result.StatusBadgeText, "#ECFDF5", "#A7F3D0", "#0F7B4B");
                }
                else
                {
                    SetConnectionBadge(result.StatusBadgeText, "#FFFBEB", "#FDE68A", "#B45309");
                }
            }
            else
            {
                SetConnectionBadge(result.StatusBadgeText, "#FEF2F2", "#FECACA", "#C42B1C");
            }

            ConfigFeedbackMessage = result.DetailMessage;
            string? detectedTargetKey = result.DetectedContextWindowTokens.HasValue
                ? draft.BuildModelTargetKey()
                : _savedSettings.DetectedContextWindowTargetKey;
            if (result.DetectedContextWindowTokens is int detectedCtx)
            {
                BuiltInAiAgentService.RememberProbedContextWindow(
                    draft.BuildModelTargetKey(),
                    detectedCtx,
                    result.DetectedContextWindowSource);
            }

            _savedSettings = _savedSettings with
            {
                CloudPrivacyAcknowledged = draft.CloudPrivacyAcknowledged,
                LastConnectionTestedUtc = DateTimeOffset.UtcNow,
                LastConnectionSucceeded = result.Success,
                LastConnectionSupportsToolCalling = result.SupportsToolCalling,
                LastConnectionSummary = result.StatusBadgeText,
                DetectedContextWindowTokens = result.DetectedContextWindowTokens ?? _savedSettings.DetectedContextWindowTokens,
                DetectedContextWindowSource = result.DetectedContextWindowSource ?? _savedSettings.DetectedContextWindowSource,
                DetectedContextWindowTargetKey = detectedTargetKey
            };
            NotifyContextWindowMetadataChanged();
        }
        catch (Exception ex)
        {
            SetConnectionBadge("连接失败", "#FEF2F2", "#FECACA", "#C42B1C");
            ConfigFeedbackMessage = AiPathSanitizer.RedactSecret(ex.Message, plainKey);
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    [RelayCommand]
    private void SaveConfig()
    {
        string? validationError = ValidateDraftConfig(out AiProviderSettings draft, out _);
        if (validationError is not null)
        {
            ConfigFeedbackMessage = validationError;
            return;
        }

        if (!EnsureCloudPrivacyAcknowledged(draft, out bool acknowledgedNow))
        {
            return;
        }
        if (acknowledgedNow)
        {
            draft = draft with { CloudPrivacyAcknowledged = true };
        }

        string? enteredKey = PendingApiKeyAccessor?.Invoke();
        string? keyToSave = string.IsNullOrWhiteSpace(enteredKey) ? null : enteredKey.Trim();

        _savedSettings = _configStore.Save(draft, keyToSave);
        ApplySettingsToForm(_savedSettings);
        ClearPendingApiKeyBoxAction?.Invoke();

        ConfigFeedbackMessage = _savedSettings.IsLocalEndpoint
            ? "已保存本地模型配置（零外发端点）。"
            : "已保存配置，API Key 已通过 Windows DPAPI（CurrentUser）加密存储。";
    }

    private void ApplySettingsToForm(AiProviderSettings settings)
    {
        SelectedPreset = settings.Preset;
        foreach (AiPresetChipItem item in PresetChips)
        {
            item.IsSelected = item.Preset == settings.Preset;
        }

        Endpoint = settings.Endpoint;
        ModelName = settings.ModelName;
        MaxToolSteps = settings.EffectiveMaxToolSteps;
        TimeoutSeconds = settings.EffectiveTimeoutSeconds;
        OverallTimeoutMinutes = settings.EffectiveOverallTimeoutMinutes;
        ContextWindowTokens = settings.EffectiveConfiguredContextWindowTokens;
        ResponseLanguage = settings.ResponseLanguage;
        ReasoningEffort = settings.ReasoningEffort;
        ChatFontSize = settings.EffectiveChatFontSize;
        MaskedApiKeyPreview = settings.MaskedApiKeyPreview ?? string.Empty;
        NotifyContextWindowMetadataChanged();

        if (settings.LastConnectionTestedUtc.HasValue)
        {
            if (settings.LastConnectionSucceeded && settings.LastConnectionSupportsToolCalling)
            {
                SetConnectionBadge(settings.LastConnectionSummary ?? "已测通 · 支持 Tool Calling", "#ECFDF5", "#A7F3D0", "#0F7B4B");
            }
            else if (settings.LastConnectionSucceeded)
            {
                SetConnectionBadge(settings.LastConnectionSummary ?? "已测通 · 不支持 Tool Calling", "#FFFBEB", "#FDE68A", "#B45309");
            }
            else
            {
                SetConnectionBadge("上次测试失败", "#FEF2F2", "#FECACA", "#C42B1C");
            }
        }
        else if (settings.IsLocalEndpoint)
        {
            SetConnectionBadge("本地零外发端点", "#ECFDF5", "#A7F3D0", "#0F7B4B");
        }
        else if (settings.HasSavedApiKey)
        {
            SetConnectionBadge("已配置密钥 · 待测试", "#EBF5FF", "#BFDBFE", "#0078D4");
        }
        else
        {
            SetConnectionBadge("未配置 API Key", "#F8FAFC", "#E2E8F0", "#64748B");
        }
    }

    private AiProviderSettings BuildDraftSettings()
    {
        int clampedIdleSeconds = Math.Clamp(TimeoutSeconds, 15, AiProviderSettings.HardMaxTimeoutSeconds);
        int clampedOverallMinutes = OverallTimeoutMinutes <= 0
            ? AiProviderSettings.DefaultOverallTimeoutMinutes
            : Math.Clamp(OverallTimeoutMinutes, AiProviderSettings.MinOverallTimeoutMinutes, AiProviderSettings.MaxOverallTimeoutMinutes);
        int overallSeconds = Math.Clamp(
            clampedOverallMinutes * 60,
            Math.Max(AiProviderSettings.MinOverallTimeoutMinutes * 60, clampedIdleSeconds),
            AiProviderSettings.HardMaxOverallTimeoutSeconds);

        return _savedSettings with
        {
            Preset = SelectedPreset,
            Endpoint = Endpoint.Trim(),
            ModelName = ModelName.Trim(),
            MaxToolSteps = MaxToolSteps <= 0 ? 0 : Math.Clamp(MaxToolSteps, 1, AiProviderSettings.HardMaxToolSteps),
            TimeoutSeconds = clampedIdleSeconds,
            OverallTimeoutSeconds = overallSeconds,
            ContextWindowTokens = ContextWindowTokens <= 0 ? 0 : Math.Clamp(ContextWindowTokens, AiProviderSettings.MinContextWindowTokens, AiProviderSettings.MaxContextWindowTokens),
            ResponseLanguage = ResponseLanguage,
            ReasoningEffort = ReasoningEffort,
            ChatFontSize = ChatFontSize
        };
    }

    private string? ValidateDraftConfig(out AiProviderSettings draft, out string? resolvedApiKey)
    {
        draft = BuildDraftSettings();
        resolvedApiKey = null;

        try
        {
            _ = OpenAiCompatibleChatClient.ResolveChatCompletionsUri(draft.Endpoint);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        if (string.IsNullOrWhiteSpace(draft.ModelName))
        {
            return "请填写模型名称（例如 deepseek-chat、qwen-plus 或 qwen2.5:14b）。";
        }

        string? pendingKey = PendingApiKeyAccessor?.Invoke();
        resolvedApiKey = !string.IsNullOrWhiteSpace(pendingKey)
            ? pendingKey.Trim()
            : _configStore.TryUnprotectApiKey(_savedSettings);

        if (!draft.IsLocalEndpoint && string.IsNullOrWhiteSpace(resolvedApiKey))
        {
            return "当前为云端模型端点，请先输入 API Key（或切换为本地 Ollama / LM Studio 零外发端点）。";
        }

        return null;
    }

    private bool EnsureCloudPrivacyAcknowledged(AiProviderSettings draft, out bool acknowledgedNow)
    {
        acknowledgedNow = false;
        if (draft.IsLocalEndpoint || draft.CloudPrivacyAcknowledged || _savedSettings.CloudPrivacyAcknowledged)
        {
            return true;
        }

        bool confirmed = CustomMessageBox.ShowQuestion(
            "你即将启用非本机（云端）AI 模型端点。\n\n" +
            "工具仅会向你配置的模型服务商发送经脱敏的目录结构与占用元数据（本机用户名替换为 <USER>），绝不上传任何文件内容、注册表密钥或凭据。\n" +
            "AI 建议仅供参考，不代表工具官方安全背书，绝不会自动执行迁移或删除。\n\n" +
            "若希望 100% 数据不出本机，请点「否」并改用本地 Ollama / LM Studio。\n\n是否确认继续使用该云端端点？",
            "云端元数据外发与隐私告知");

        if (!confirmed)
        {
            ConfigFeedbackMessage = "已取消云端端点启用；如需 100% 离线分析，可选择「本地 Ollama」或「LM Studio」。";
            return false;
        }

        acknowledgedNow = true;
        return true;
    }

    private void SetConnectionBadge(string text, string bg, string border, string fg)
    {
        ConnectionBadgeText = text;
        ConnectionBadgeBg = bg;
        ConnectionBadgeBorder = border;
        ConnectionBadgeFg = fg;
    }
}
