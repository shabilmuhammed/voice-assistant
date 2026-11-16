using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hark.Core;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = Markdig.Syntax.Block;
using Inline = Markdig.Syntax.Inlines.Inline;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;
using WpfTableRow = System.Windows.Documents.TableRow;
using WpfTableCell = System.Windows.Documents.TableCell;

namespace Hark.App.Controls;

/// <summary>
/// Shows Markdown as formatted, selectable text: headings, lists, tables, code blocks and links.
/// Colours come from the Windows theme, so it works in light and dark mode.
/// </summary>
public sealed class MarkdownView : RichTextBox
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseAutoLinks().UseEmphasisExtras().UseTaskLists().Build();

    private static FontFamily Mono => Application.Current?.TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Cascadia Code, Consolas");

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata("", (d, _) => ((MarkdownView)d).Schedule()));

    private readonly DispatcherTimer _timer;
    private string _rendered = "\0";

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true; // lets links be clicked
        // Streaming answers change many times a second; re-render at most every 80 ms.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += (_, _) => { _timer.Stop(); Render(); };
        Loaded += (_, _) => Render();
        AddHandler(Hyperlink.RequestNavigateEvent, new System.Windows.Navigation.RequestNavigateEventHandler(OnNavigate));
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Schedule()
    {
        if (!IsLoaded) return;
        if (!_timer.IsEnabled) _timer.Start();
    }

    // The text box would otherwise swallow the mouse wheel, so the chat could not be scrolled over an answer.
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        var parent = VisualTreeHelper.GetParent(this) as UIElement;
        parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
    }

    private static void OnNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (e.Uri is { IsAbsoluteUri: true } uri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Opening a link failed", ex);
        }
    }

    private void Render()
    {
        var text = Markdown ?? "";
        if (text == _rendered) return;
        _rendered = text;

        var document = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };
        try
        {
            foreach (var block in Markdig.Markdown.Parse(text, Pipeline)) Append(document.Blocks, block);
        }
        catch (Exception ex)
        {
            Log.Error("Rendering Markdown failed; showing plain text", ex);
            document.Blocks.Clear();
            document.Blocks.Add(new Paragraph(new Run(text)));
        }
        if (document.Blocks.FirstBlock is { } first) first.Margin = new Thickness(0, 0, 0, first.Margin.Bottom);
        if (document.Blocks.LastBlock is { } last) last.Margin = new Thickness(0, last.Margin.Top, 0, 0);
        Document = document;
    }

    // ---- blocks -------------------------------------------------------------------

    private void Append(BlockCollection target, Block block)
    {
        switch (block)
        {
            case HeadingBlock h:
                var heading = new Paragraph
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = h.Level switch { 1 => 20, 2 => 17, 3 => 15.5, _ => 14.5 },
                    Margin = new Thickness(0, 12, 0, 6),
                };
                AddInlines(heading.Inlines, h.Inline);
                target.Add(heading);
                break;

            case ParagraphBlock p:
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                AddInlines(paragraph.Inlines, p.Inline);
                target.Add(paragraph);
                break;

            case FencedCodeBlock or CodeBlock:
                target.Add(Code(((LeafBlock)block).Lines.ToString(), (block as FencedCodeBlock)?.Info));
                break;

            case ListBlock list:
                var wpfList = new List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, 10),
                    Padding = new Thickness(22, 0, 0, 0),
                };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start)) wpfList.StartIndex = start;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var listItem = new ListItem();
                    foreach (var child in item) Append(listItem.Blocks, child);
                    foreach (var child in listItem.Blocks) child.Margin = new Thickness(0, 0, 0, 3);
                    wpfList.ListItems.Add(listItem);
                }
                target.Add(wpfList);
                break;

            case QuoteBlock quote:
                var section = new Section { Padding = new Thickness(12, 0, 0, 0), BorderThickness = new Thickness(2, 0, 0, 0), Margin = new Thickness(0, 0, 0, 10) };
                section.SetResourceReference(Section.BorderBrushProperty, "H.Line");
                section.SetResourceReference(TextElement.ForegroundProperty, "H.Ink2");
                foreach (var child in quote) Append(section.Blocks, child);
                target.Add(section);
                break;

            case Markdig.Extensions.Tables.Table table:
                target.Add(Table(table));
                break;

            case ThematicBreakBlock:
                var rule = new Paragraph { Margin = new Thickness(0, 4, 0, 12), FontSize = 1, BorderThickness = new Thickness(0, 1, 0, 0) };
                rule.SetResourceReference(WpfBlock.BorderBrushProperty, "H.Line");
                target.Add(rule);
                break;

            case ContainerBlock container:
                foreach (var child in container) Append(target, child);
                break;

            case LeafBlock leaf:
                target.Add(new Paragraph(new Run(leaf.Lines.ToString())) { Margin = new Thickness(0, 0, 0, 10) });
                break;
        }
    }

    /// <summary>A code block: a header with the language and a Copy button, then the code, which scrolls sideways.</summary>
    private static WpfBlock Code(string code, string? language)
    {
        code = code.TrimEnd('\r', '\n');

        var label = new TextBlock { Text = string.IsNullOrWhiteSpace(language) ? "code" : language.Trim(), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "H.Ink2");

        var copy = new Button { Content = "Copy" };
        copy.SetResourceReference(StyleProperty, "H.Ghost");
        copy.Click += async (_, _) =>
        {
            try
            {
                Clipboard.SetText(code);
                copy.Content = "Copied";
                await Task.Delay(1400);
                copy.Content = "Copy";
            }
            catch (Exception ex)
            {
                Log.Error("Copying code failed", ex);
            }
        };

        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        header.Children.Add(label);
        var headerBar = new Border { Padding = new Thickness(12, 4, 6, 4), CornerRadius = new CornerRadius(9, 9, 0, 0), Child = header };
        headerBar.SetResourceReference(Border.BackgroundProperty, "H.Sunken");

        var text = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = Mono,
            FontSize = 12.8,
            Padding = new Thickness(12, 10, 12, 12),
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AcceptsReturn = true,
        };
        text.SetResourceReference(ForegroundProperty, "H.Ink");
        text.SetResourceReference(StyleProperty, "SelectableText");
        text.TextWrapping = TextWrapping.NoWrap;
        text.FontSize = 12.8;
        text.FontFamily = Mono;
        text.Padding = new Thickness(12, 10, 12, 12);

        var stack = new StackPanel();
        stack.Children.Add(headerBar);
        stack.Children.Add(text);
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = stack };
        frame.SetResourceReference(Border.BorderBrushProperty, "H.Line");
        frame.SetResourceReference(Border.BackgroundProperty, "H.Surface");

        return new BlockUIContainer(frame) { Margin = new Thickness(0, 2, 0, 12) };
    }

    private WpfTable Table(Markdig.Extensions.Tables.Table source)
    {
        var table = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 12), BorderThickness = new Thickness(1, 1, 0, 0) };
        table.SetResourceReference(WpfBlock.BorderBrushProperty, "H.Line");
        var columns = source.OfType<Markdig.Extensions.Tables.TableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (var i = 0; i < columns; i++) table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        foreach (var sourceRow in source.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var row = new WpfTableRow();
            if (sourceRow.IsHeader)
            {
                row.FontWeight = FontWeights.SemiBold;
                row.SetResourceReference(TextElement.BackgroundProperty, "H.Sunken");
            }
            foreach (var sourceCell in sourceRow.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var cell = new WpfTableCell { Padding = new Thickness(9, 5, 9, 5), BorderThickness = new Thickness(0, 0, 1, 1) };
                cell.SetResourceReference(WpfTableCell.BorderBrushProperty, "H.Line");
                foreach (var child in sourceCell) Append(cell.Blocks, child);
                foreach (var child in cell.Blocks) child.Margin = new Thickness(0);
                if (cell.Blocks.Count == 0) cell.Blocks.Add(new Paragraph());
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    // ---- inlines ------------------------------------------------------------------

    private void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null) return;
        foreach (var inline in container) AddInline(target, inline);
    }

    private void AddInline(InlineCollection target, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;

            case CodeInline code:
                var run = new Run(code.Content) { FontFamily = Mono, FontSize = 12.8 };
                run.SetResourceReference(TextElement.BackgroundProperty, "H.Sunken");
                target.Add(run);
                break;

            case EmphasisInline emphasis:
                Span span = emphasis.DelimiterChar switch
                {
                    '~' => new Span { TextDecorations = TextDecorations.Strikethrough },
                    _ when emphasis.DelimiterCount >= 2 => new Bold(),
                    _ => new Italic(),
                };
                AddInlines(span.Inlines, emphasis);
                target.Add(span);
                break;

            case LinkInline { IsImage: false } link:
                var hyperlink = new Hyperlink();
                if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)) hyperlink.NavigateUri = uri;
                hyperlink.ToolTip = link.Url;
                hyperlink.SetResourceReference(TextElement.ForegroundProperty, "H.Signal");
                AddInlines(hyperlink.Inlines, link);
                if (hyperlink.Inlines.Count == 0) hyperlink.Inlines.Add(new Run(link.Url ?? ""));
                target.Add(hyperlink);
                break;

            case LinkInline image:
                target.Add(new Run($"[image: {image.Url}]"));
                break;

            case AutolinkInline auto:
                var autoLink = new Hyperlink(new Run(auto.Url)) { ToolTip = auto.Url };
                if (Uri.TryCreate(auto.Url, UriKind.Absolute, out var autoUri)) autoLink.NavigateUri = autoUri;
                autoLink.SetResourceReference(TextElement.ForegroundProperty, "H.Signal");
                target.Add(autoLink);
                break;

            case LineBreakInline lineBreak:
                if (lineBreak.IsHard) target.Add(new LineBreak());
                else target.Add(new Run(" "));
                break;

            case TaskList task:
                target.Add(new Run(task.Checked ? "☑ " : "☐ "));
                break;

            case HtmlInline html:
                target.Add(new Run(html.Tag));
                break;

            case HtmlEntityInline entity:
                target.Add(new Run(entity.Transcoded.ToString()));
                break;

            case ContainerInline nested:
                AddInlines(target, nested);
                break;

            default:
                target.Add(new Run(inline.ToString() ?? ""));
                break;
        }
    }
}
