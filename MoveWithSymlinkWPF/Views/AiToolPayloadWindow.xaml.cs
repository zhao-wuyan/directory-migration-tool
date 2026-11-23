using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

public partial class AiToolPayloadWindow : Window
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(
            UnicodeRanges.BasicLatin,
            UnicodeRanges.GeneralPunctuation,
            UnicodeRanges.CjkSymbolsandPunctuation,
            UnicodeRanges.CjkUnifiedIdeographs,
            UnicodeRanges.HalfwidthandFullwidthForms)
    };

    private readonly string _formattedPayload;

    public AiToolPayloadWindow(AiToolCallStepItem step)
    {
        InitializeComponent();

        HeaderTitleText.Text = step.DetailHeaderText;
        ArgumentsText.Text = step.DisplayArguments;

        _formattedPayload = FormatPayloadForDisplay(step.ResultPayload, step.ResultSummary);
        PayloadTextBox.Text = _formattedPayload;

        int charCount = _formattedPayload.Length;
        string elapsed = step.ElapsedMilliseconds > 0 ? $" · 耗时 {step.ElapsedMilliseconds} ms" : string.Empty;
        PayloadMetaText.Text = $"共 {charCount:N0} 字符{elapsed}";

        if (step.IsRunning)
        {
            ApplyBadge("执行中…", "#EBF5FF", "#BFDBFE", "#0078D4");
        }
        else if (step.Succeeded)
        {
            ApplyBadge("已返回只读数据", "#ECFDF5", "#A7F3D0", "#0F7B4B");
        }
        else
        {
            ApplyBadge("已拦截 / 失败", "#FEF2F2", "#FECACA", "#C42B1C");
        }
    }

    public static string FormatPayloadForDisplay(string? rawPayload, string? fallbackSummary = null)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return string.IsNullOrWhiteSpace(fallbackSummary)
                ? "（当前步骤暂无可展示的原始返回数据）"
                : fallbackSummary;
        }

        string trimmed = rawPayload.Trim();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(trimmed);
            return JsonSerializer.Serialize(doc.RootElement, PrettyJsonOptions);
        }
        catch
        {
            return trimmed;
        }
    }

    private void ApplyBadge(string text, string bgHex, string borderHex, string fgHex)
    {
        StatusBadgeText.Text = text;
        StatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgHex));
        StatusBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderHex));
        StatusBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fgHex));
    }

    private void CopyPayloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_formattedPayload);
            CopyFeedbackText.Text = "✓ 已复制到剪贴板";
        }
        catch
        {
            CopyFeedbackText.Text = "复制失败，请手动选中文本框内容复制";
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
