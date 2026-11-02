using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Help;
using MyTextEditor.Models;
using Application = System.Windows.Application;

internal static class HelpLayoutVerification
{
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var window = new HelpWindow { Width = 760, Height = 500, ShowInTaskbar = false };
        try
        {
            window.Show();
            foreach (var theme in new[] { "Light", "Dark" })
            {
                app.Resources.MergedDictionaries[0] = ThemePalette.Get(theme).CreateResources();
                foreach (var topic in HelpCatalog.Topics)
                {
                    window.NavigateToTopic(topic.Id);
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var scroll = (ScrollViewer)typeof(HelpWindow).GetField("TopicScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    if (scroll.ExtentWidth > scroll.ViewportWidth + 1) throw new InvalidOperationException("Help content overflows: " + topic.Id);
                    if (topic.Sections.Any(section => section.Body.Contains("\\n"))) throw new InvalidOperationException("Literal newline escape in help");
                }
            }
            foreach (var query in new[] { "순서", "인코딩", "Ctrl+W", "병합", "자동 줄바꿈" })
                if (HelpCatalog.Search(HelpCatalog.Topics, query).Count == 0) throw new InvalidOperationException("Missing help: " + query);
            app.Resources.MergedDictionaries[0] = ThemePalette.Get("Light").CreateResources();
            window.NavigateToTopic("shortcuts");
            window.UpdateLayout();
            Directory.CreateDirectory("artifacts");
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create("artifacts/help-layout.png");
            encoder.Save(stream);
            Console.WriteLine("PASS help topics, search, Light/Dark minimum-width layout and screenshot");
            return 0;
        }
        finally { window.Close(); app.Shutdown(); }
    }
}
