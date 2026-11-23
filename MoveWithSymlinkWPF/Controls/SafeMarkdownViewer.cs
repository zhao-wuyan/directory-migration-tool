using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MigrationCore.Services.Ai;

namespace MoveWithSymlinkWPF.Controls;

/// <summary>
/// 面向高完整性（requireAdministrator）WPF 进程的安全受限 Markdown 渲染控件：
/// 1. 物理上绝不创建 Hyperlink 或触发任何 Shell/URI 执行，链接仅展示安全纯文本标签；
/// 2. 零第三方依赖，直接生成轻量原生 WPF 块级元素，融入外部 ScrollViewer 不吞滚轮；
/// 3. 内置 60ms 流式防抖（首次与静态赋值立即同步渲染），兼顾 SSE 流式丝滑度与零卡顿；
/// 4. 支持标题、段落、加粗、斜体、删除线、行内代码、无序/有序多级列表、引用块、围栏代码块、GFM 表格与分割线。
/// </summary>
public sealed partial class SafeMarkdownViewer : UserControl
{
    public static readonly DependencyProperty MarkdownTextProperty = DependencyProperty.Register(
        nameof(MarkdownText),
        typeof(string),
        typeof(SafeMarkdownViewer),
        new PropertyMetadata(string.Empty, OnMarkdownTextChanged));

    public static readonly DependencyProperty BaseFontSizeProperty = DependencyProperty.Register(
        nameof(BaseFontSize),
        typeof(double),
        typeof(SafeMarkdownViewer),
        new PropertyMetadata(11.5, OnVisualStyleChanged));

    public static readonly DependencyProperty TextForegroundProperty = DependencyProperty.Register(
        nameof(TextForeground),
        typeof(Brush),
        typeof(SafeMarkdownViewer),
        new PropertyMetadata(CreateFrozenBrush("#334155"), OnVisualStyleChanged));

    private static readonly SolidColorBrush HeadingBrush = CreateFrozenBrush("#0F172A");
    private static readonly SolidColorBrush StrongBrush = CreateFrozenBrush("#0F172A");
    private static readonly SolidColorBrush MutedBrush = CreateFrozenBrush("#64748B");
    private static readonly SolidColorBrush AccentBrush = CreateFrozenBrush("#0078D4");
    private static readonly SolidColorBrush CodeBgBrush = CreateFrozenBrush("#F1F5F9");
    private static readonly SolidColorBrush CodeBlockBgBrush = CreateFrozenBrush("#F8FAFC");
    private static readonly SolidColorBrush CodeBorderBrush = CreateFrozenBrush("#E2E8F0");
    private static readonly SolidColorBrush CodeFgBrush = CreateFrozenBrush("#0F172A");
    private static readonly SolidColorBrush QuoteBgBrush = CreateFrozenBrush("#F8FAFC");
    private static readonly SolidColorBrush QuoteBarBrush = CreateFrozenBrush("#93C5FD");
    private static readonly SolidColorBrush DividerBrush = CreateFrozenBrush("#E2E8F0");
    private static readonly SolidColorBrush TableHeaderBgBrush = CreateFrozenBrush("#F1F5F9");
    private static readonly SolidColorBrush TableRowAltBgBrush = CreateFrozenBrush("#FAFBFD");
    private static readonly FontFamily CodeFontFamily = new("Consolas, Cascadia Mono, Microsoft YaHei UI");

    private const int StreamDebounceMs = 60;

    private readonly StackPanel _rootPanel;
    private DispatcherTimer? _debounceTimer;
    private DateTime _lastRenderUtc = DateTime.MinValue;
    private string _lastRenderedText = string.Empty;

    [GeneratedRegex(@"^(#{1,4})\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(\s*)([-*+•])\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex UnorderedListRegex();

    [GeneratedRegex(@"^(\s*)(\d+[.)])\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex OrderedListRegex();

    [GeneratedRegex(@"^\s*([-*_])(?:\s*\1){2,}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"^\s*\|?(?:\s*:?-{3,}:?\s*\|)+\s*:?-{3,}:?\s*\|?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TableSeparatorRegex();

    public string MarkdownText
    {
        get => (string)GetValue(MarkdownTextProperty);
        set => SetValue(MarkdownTextProperty, value);
    }

    public double BaseFontSize
    {
        get => (double)GetValue(BaseFontSizeProperty);
        set => SetValue(BaseFontSizeProperty, value);
    }

    public Brush TextForeground
    {
        get => (Brush)GetValue(TextForegroundProperty);
        set => SetValue(TextForegroundProperty, value);
    }

    public SafeMarkdownViewer()
    {
        _rootPanel = new StackPanel
        {
            Orientation = Orientation.Vertical
        };
        Content = _rootPanel;
        Focusable = false;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_debounceTimer is not null)
        {
            _debounceTimer.Stop();
            _debounceTimer = null;
        }
    }

    private static void OnMarkdownTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SafeMarkdownViewer viewer)
        {
            viewer.ScheduleRender(e.NewValue as string ?? string.Empty);
        }
    }

    private static void OnVisualStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SafeMarkdownViewer viewer)
        {
            viewer.RenderNow(viewer.MarkdownText ?? string.Empty);
        }
    }

    private void ScheduleRender(string rawText)
    {
        if (string.IsNullOrEmpty(rawText) || (DateTime.UtcNow - _lastRenderUtc).TotalMilliseconds >= StreamDebounceMs)
        {
            _debounceTimer?.Stop();
            RenderNow(rawText);
            return;
        }

        if (_debounceTimer is null)
        {
            _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(StreamDebounceMs)
            };
            _debounceTimer.Tick += (_, _) =>
            {
                _debounceTimer?.Stop();
                RenderNow(MarkdownText ?? string.Empty);
            };
        }

        if (!_debounceTimer.IsEnabled)
        {
            _debounceTimer.Start();
        }
    }

    private void RenderNow(string rawText)
    {
        _lastRenderUtc = DateTime.UtcNow;
        if (string.Equals(rawText, _lastRenderedText, StringComparison.Ordinal) && _rootPanel.Children.Count > 0)
        {
            return;
        }

        _lastRenderedText = rawText;
        _rootPanel.Children.Clear();

        if (string.IsNullOrWhiteSpace(rawText))
        {
            return;
        }

        string safeText = AiPathSanitizer.SanitizeUntrustedText(rawText, maxLength: 16_000, preserveNewLines: true);
        string[] lines = safeText.Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (string.IsNullOrEmpty(trimmed))
            {
                i++;
                continue;
            }

            // 1. 围栏代码块（支持流式未闭合块）
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                string fence = trimmed.StartsWith("```", StringComparison.Ordinal) ? "```" : "~~~";
                string lang = trimmed[fence.Length..].Trim();
                var codeLines = new List<string>();
                i++;
                while (i < lines.Length)
                {
                    if (lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                    {
                        i++;
                        break;
                    }
                    codeLines.Add(lines[i]);
                    i++;
                }
                _rootPanel.Children.Add(BuildCodeBlock(lang, string.Join("\n", codeLines)));
                continue;
            }

            // 2. 标题（# ~ ####）
            Match headingMatch = HeadingRegex().Match(trimmed);
            if (headingMatch.Success)
            {
                int level = headingMatch.Groups[1].Value.Length;
                string content = headingMatch.Groups[2].Value.Trim();
                _rootPanel.Children.Add(BuildHeading(level, content));
                i++;
                continue;
            }

            // 3. 水平分割线
            if (HorizontalRuleRegex().IsMatch(trimmed))
            {
                _rootPanel.Children.Add(new Border
                {
                    Height = 1,
                    Background = DividerBrush,
                    Margin = new Thickness(0, 6, 0, 6)
                });
                i++;
                continue;
            }

            // 4. 引用块（连续 > 行）
            if (trimmed.StartsWith('>'))
            {
                var quoteLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    string q = lines[i].TrimStart();
                    q = q.Length > 1 && q[1] == ' ' ? q[2..] : q[1..];
                    quoteLines.Add(q);
                    i++;
                }
                _rootPanel.Children.Add(BuildQuoteBlock(quoteLines));
                continue;
            }

            // 5. GFM 表格
            if (line.Contains('|') && i + 1 < lines.Length && TableSeparatorRegex().IsMatch(lines[i + 1]))
            {
                string[] headers = SplitTableRow(line);
                i += 2; // 跳过表头与分隔行
                var rows = new List<string[]>();
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && lines[i].Contains('|'))
                {
                    rows.Add(SplitTableRow(lines[i]));
                    i++;
                }
                if (headers.Length > 0)
                {
                    _rootPanel.Children.Add(BuildTableBlock(headers, rows));
                    continue;
                }
            }

            // 6. 无序列表项
            Match ulMatch = UnorderedListRegex().Match(line);
            if (ulMatch.Success)
            {
                int indentLevel = Math.Min(3, ulMatch.Groups[1].Value.Length / 2);
                string itemText = ulMatch.Groups[3].Value.Trim();
                _rootPanel.Children.Add(BuildListItem("•", itemText, indentLevel, isOrdered: false));
                i++;
                continue;
            }

            // 7. 有序列表项
            Match olMatch = OrderedListRegex().Match(line);
            if (olMatch.Success)
            {
                int indentLevel = Math.Min(3, olMatch.Groups[1].Value.Length / 2);
                string marker = olMatch.Groups[2].Value;
                string itemText = olMatch.Groups[3].Value.Trim();
                _rootPanel.Children.Add(BuildListItem(marker, itemText, indentLevel, isOrdered: true));
                i++;
                continue;
            }

            // 8. 普通段落（合并连续普通文本行）
            var paraLines = new List<string> { line.TrimEnd() };
            i++;
            while (i < lines.Length)
            {
                string next = lines[i];
                string nextTrim = next.Trim();
                if (string.IsNullOrEmpty(nextTrim)
                    || nextTrim.StartsWith("```", StringComparison.Ordinal)
                    || nextTrim.StartsWith("~~~", StringComparison.Ordinal)
                    || nextTrim.StartsWith('>')
                    || HeadingRegex().IsMatch(nextTrim)
                    || HorizontalRuleRegex().IsMatch(nextTrim)
                    || UnorderedListRegex().IsMatch(next)
                    || OrderedListRegex().IsMatch(next)
                    || (next.Contains('|') && i + 1 < lines.Length && TableSeparatorRegex().IsMatch(lines[i + 1])))
                {
                    break;
                }
                paraLines.Add(next.TrimEnd());
                i++;
            }

            _rootPanel.Children.Add(BuildParagraph(paraLines));
        }
    }

    private UIElement BuildHeading(int level, string text)
    {
        double baseSize = BaseFontSize;
        (double size, double topMargin, double bottomMargin, FontWeight weight) = level switch
        {
            1 => (baseSize + 2.5, 8, 4, FontWeights.Bold),
            2 => (baseSize + 1.5, 7, 3, FontWeights.Bold),
            3 => (baseSize + 0.5, 6, 2, FontWeights.SemiBold),
            _ => (baseSize, 5, 2, FontWeights.SemiBold)
        };

        var tb = new TextBlock
        {
            FontSize = size,
            FontWeight = weight,
            Foreground = HeadingBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = size + 6,
            Margin = new Thickness(0, _rootPanel.Children.Count == 0 ? 0 : topMargin, 0, bottomMargin)
        };
        AppendInlines(tb.Inlines, text, HeadingBrush, size, weight);
        return tb;
    }

    private UIElement BuildParagraph(List<string> lines)
    {
        var tb = new TextBlock
        {
            FontSize = BaseFontSize,
            Foreground = TextForeground,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = BaseFontSize + 7,
            Margin = new Thickness(0, 0, 0, 5)
        };

        for (int idx = 0; idx < lines.Count; idx++)
        {
            if (idx > 0)
            {
                tb.Inlines.Add(new LineBreak());
            }
            AppendInlines(tb.Inlines, lines[idx], TextForeground, BaseFontSize, FontWeights.Normal);
        }
        return tb;
    }

    private UIElement BuildListItem(string marker, string text, int indentLevel, bool isOrdered)
    {
        var grid = new Grid
        {
            Margin = new Thickness(indentLevel * 12, 0, 0, 3)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var markerBlock = new TextBlock
        {
            Text = marker,
            FontSize = BaseFontSize,
            FontWeight = isOrdered ? FontWeights.SemiBold : FontWeights.Bold,
            Foreground = AccentBrush,
            Margin = new Thickness(0, 0, 6, 0),
            MinWidth = isOrdered ? 18 : 10,
            LineHeight = BaseFontSize + 7
        };
        Grid.SetColumn(markerBlock, 0);

        var contentBlock = new TextBlock
        {
            FontSize = BaseFontSize,
            Foreground = TextForeground,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = BaseFontSize + 7
        };
        AppendInlines(contentBlock.Inlines, text, TextForeground, BaseFontSize, FontWeights.Normal);
        Grid.SetColumn(contentBlock, 1);

        grid.Children.Add(markerBlock);
        grid.Children.Add(contentBlock);
        return grid;
    }

    private UIElement BuildQuoteBlock(List<string> quoteLines)
    {
        var tb = new TextBlock
        {
            FontSize = Math.Max(10.5, BaseFontSize - 0.5),
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = BaseFontSize + 6
        };

        for (int idx = 0; idx < quoteLines.Count; idx++)
        {
            if (idx > 0)
            {
                tb.Inlines.Add(new LineBreak());
            }
            AppendInlines(tb.Inlines, quoteLines[idx], MutedBrush, tb.FontSize, FontWeights.Normal);
        }

        return new Border
        {
            Background = QuoteBgBrush,
            BorderBrush = QuoteBarBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 6, 6, 0),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 2, 0, 6),
            Child = tb
        };
    }

    private UIElement BuildCodeBlock(string language, string code)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        if (!string.IsNullOrWhiteSpace(language))
        {
            stack.Children.Add(new TextBlock
            {
                Text = language,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = MutedBrush,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        var codeText = new TextBlock
        {
            Text = code,
            FontFamily = CodeFontFamily,
            FontSize = Math.Max(10, BaseFontSize - 1),
            Foreground = CodeFgBrush,
            TextWrapping = TextWrapping.NoWrap,
            LineHeight = BaseFontSize + 5
        };

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = codeText
        };
        scroller.PreviewMouseWheel += ForwardMouseWheelToParent;
        stack.Children.Add(scroller);

        return new Border
        {
            Background = CodeBlockBgBrush,
            BorderBrush = CodeBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 2, 0, 6),
            Child = stack
        };
    }

    private UIElement BuildTableBlock(string[] headers, List<string[]> rows)
    {
        int colCount = headers.Length;
        foreach (string[] r in rows)
        {
            if (r.Length > colCount) colCount = r.Length;
        }
        colCount = Math.Clamp(colCount, 1, 8);

        var grid = new Grid();
        for (int c = 0; c < colCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = 56
            });
        }

        int totalRows = 1 + rows.Count;
        for (int r = 0; r < totalRows; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        double cellFontSize = Math.Max(10, BaseFontSize - 1);

        // 表头行
        for (int c = 0; c < colCount; c++)
        {
            string cellText = c < headers.Length ? headers[c] : string.Empty;
            var tb = new TextBlock
            {
                FontSize = cellFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = HeadingBrush,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = cellFontSize + 5
            };
            AppendInlines(tb.Inlines, cellText, HeadingBrush, cellFontSize, FontWeights.SemiBold);

            var cellBorder = new Border
            {
                Background = TableHeaderBgBrush,
                BorderBrush = DividerBrush,
                BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, 1),
                Padding = new Thickness(6, 4, 6, 4),
                Child = tb
            };
            Grid.SetRow(cellBorder, 0);
            Grid.SetColumn(cellBorder, c);
            grid.Children.Add(cellBorder);
        }

        // 数据行
        for (int r = 0; r < rows.Count; r++)
        {
            string[] rowCells = rows[r];
            Brush rowBg = r % 2 == 1 ? TableRowAltBgBrush : Brushes.Transparent;
            bool isLastRow = r == rows.Count - 1;

            for (int c = 0; c < colCount; c++)
            {
                string cellText = c < rowCells.Length ? rowCells[c] : string.Empty;
                var tb = new TextBlock
                {
                    FontSize = cellFontSize,
                    Foreground = TextForeground,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = cellFontSize + 5
                };
                AppendInlines(tb.Inlines, cellText, TextForeground, cellFontSize, FontWeights.Normal);

                var cellBorder = new Border
                {
                    Background = rowBg,
                    BorderBrush = DividerBrush,
                    BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, isLastRow ? 0 : 1),
                    Padding = new Thickness(6, 4, 6, 4),
                    Child = tb
                };
                Grid.SetRow(cellBorder, r + 1);
                Grid.SetColumn(cellBorder, c);
                grid.Children.Add(cellBorder);
            }
        }

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = grid
        };
        scroller.PreviewMouseWheel += ForwardMouseWheelToParent;

        return new Border
        {
            BorderBrush = CodeBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 2, 0, 6),
            Child = scroller
        };
    }

    /// <summary>
    /// 安全行内 Markdown 解析器：绝不创建 Hyperlink，故意不对单下划线触发斜体以保护 Windows 目录名中的下划线。
    /// </summary>
    private static void AppendInlines(
        InlineCollection inlines,
        string text,
        Brush baseForeground,
        double fontSize,
        FontWeight baseWeight)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var plainBuffer = new StringBuilder();
        int i = 0;
        int len = text.Length;

        void FlushPlain()
        {
            if (plainBuffer.Length == 0) return;
            inlines.Add(new Run(plainBuffer.ToString())
            {
                Foreground = baseForeground,
                FontWeight = baseWeight
            });
            plainBuffer.Clear();
        }

        while (i < len)
        {
            char ch = text[i];

            // 1. 行内代码 `code`
            if (ch == '`')
            {
                int close = text.IndexOf('`', i + 1);
                if (close > i + 1)
                {
                    FlushPlain();
                    string code = text[(i + 1)..close];
                    inlines.Add(new Run($" {code} ")
                    {
                        FontFamily = CodeFontFamily,
                        FontSize = Math.Max(10, fontSize - 0.5),
                        Background = CodeBgBrush,
                        Foreground = CodeFgBrush,
                        FontWeight = FontWeights.Normal
                    });
                    i = close + 1;
                    continue;
                }
            }

            // 2. Markdown 链接 [label](url) —— 严禁创建 Hyperlink，仅安全渲染展示文本
            if (ch == '[')
            {
                int closeBracket = text.IndexOf(']', i + 1);
                if (closeBracket > i + 1 && closeBracket + 1 < len && text[closeBracket + 1] == '(')
                {
                    int closeParen = text.IndexOf(')', closeBracket + 2);
                    if (closeParen > closeBracket + 2)
                    {
                        FlushPlain();
                        string label = text[(i + 1)..closeBracket];
                        inlines.Add(new Run(label)
                        {
                            Foreground = AccentBrush,
                            FontWeight = FontWeights.Medium
                        });
                        i = closeParen + 1;
                        continue;
                    }
                }
            }

            // 3. 加粗 **text** 或 __text__
            if ((ch == '*' && i + 1 < len && text[i + 1] == '*')
                || (ch == '_' && i + 1 < len && text[i + 1] == '_'))
            {
                string marker = ch == '*' ? "**" : "__";
                int close = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    FlushPlain();
                    string inner = text[(i + 2)..close];
                    AppendInlines(inlines, inner, StrongBrush, fontSize, FontWeights.Bold);
                    i = close + 2;
                    continue;
                }
            }

            // 4. 删除线 ~~text~~
            if (ch == '~' && i + 1 < len && text[i + 1] == '~')
            {
                int close = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    FlushPlain();
                    string inner = text[(i + 2)..close];
                    inlines.Add(new Run(inner)
                    {
                        Foreground = MutedBrush,
                        TextDecorations = TextDecorations.Strikethrough
                    });
                    i = close + 2;
                    continue;
                }
            }

            // 5. 斜体 *text*（排除前后空格防止误伤乘号/项目符号）
            if (ch == '*' && i + 1 < len && !char.IsWhiteSpace(text[i + 1]))
            {
                int close = text.IndexOf('*', i + 1);
                if (close > i + 1 && !char.IsWhiteSpace(text[close - 1]))
                {
                    FlushPlain();
                    string inner = text[(i + 1)..close];
                    inlines.Add(new Run(inner)
                    {
                        Foreground = baseForeground,
                        FontWeight = baseWeight,
                        FontStyle = FontStyles.Italic
                    });
                    i = close + 1;
                    continue;
                }
            }

            plainBuffer.Append(ch);
            i++;
        }

        FlushPlain();
    }

    private static string[] SplitTableRow(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(cell => cell.Trim()).ToArray();
    }

    private static void ForwardMouseWheelToParent(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject d) return;
        DependencyObject? parent = VisualTreeHelper.GetParent(d);
        if (parent is UIElement parentElement)
        {
            e.Handled = true;
            var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            };
            parentElement.RaiseEvent(forwarded);
        }
    }

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
