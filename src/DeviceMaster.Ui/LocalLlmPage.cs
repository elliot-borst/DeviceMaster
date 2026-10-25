using System.Windows;
using System.Windows.Controls;

namespace DeviceMaster.Ui;

/// <summary>
/// The Local LLM tab: the local inference server (NInfer, when its control script exists) and
/// pausing local AI while you game, on one page. The two pages keep their own logic and per-second
/// Tick; this class only lays their sections out together, server first.
/// </summary>
public static class LocalLlmPage
{
    public static UIElement Build(NInferPage? ninfer, AiPausePage? pause)
    {
        var page = new StackPanel { MaxWidth = 1460, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(MainWindow.PageTitle("Local LLM", "your local AI server, and pausing it while you game"));

        UIElement? requests = null;
        if (ninfer is not null)
        {
            var (server, tiles, recent) = ninfer.BuildSections();
            page.Children.Add(server);
            page.Children.Add(tiles);
            requests = recent;
        }

        if (pause is not null)
        {
            var sections = pause.BuildSections();
            page.Children.Add(SectionLabel("Pause for games", "frees GPU memory from local AI while a game runs, and brings it back after"));
            page.Children.Add(sections.State);
            page.Children.Add(sections.Ask);
            page.Children.Add(sections.Suggest);
            if (requests is not null)
            {
                page.Children.Add(requests);
                ((FrameworkElement)requests).Margin = new Thickness(0, 0, 12, 16);
            }

            page.Children.Add(sections.Games);
            page.Children.Add(sections.Apps);
            page.Children.Add(sections.Settings);
            page.Children.Add(sections.Activity);
        }
        else if (requests is not null)
        {
            page.Children.Add(requests);
        }

        return page;
    }

    private static TextBlock SectionLabel(string title, string subtitle)
    {
        var block = new TextBlock { Margin = new Thickness(2, 10, 0, 12), TextWrapping = TextWrapping.Wrap };
        block.Inlines.Add(new System.Windows.Documents.Run(title) { FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text });
        block.Inlines.Add(new System.Windows.Documents.Run($"   {subtitle}") { FontSize = 12, Foreground = Theme.Faint });
        return block;
    }
}
