using Einsatzcockpit.PrintAgent.Core.Configuration;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace Einsatzcockpit.PrintAgent.Rendering;

public sealed class EdgeHtmlRenderer
{
    private readonly string? _configuredPath;
    private readonly int _readyTimeoutSeconds;
    private readonly ILogger<EdgeHtmlRenderer> _logger;

    public EdgeHtmlRenderer(string? configuredPath, int readyTimeoutSeconds, ILogger<EdgeHtmlRenderer> logger)
    {
        _configuredPath = configuredPath;
        _readyTimeoutSeconds = readyTimeoutSeconds;
        _logger = logger;
    }

    public async Task RenderAsync(string url, string targetPath, CancellationToken cancellationToken)
    {
        var edgePath = FindEdge();
        if (edgePath is null)
        {
            throw new InvalidOperationException(
                "Microsoft Edge wurde nicht gefunden. Browser-Download ist deaktiviert.");
        }

        var options = new LaunchOptions
        {
            Headless = true,
            ExecutablePath = edgePath
        };
        await using var browser = await Puppeteer.LaunchAsync(options);
        await using var page = await browser.NewPageAsync();
        await page.GoToAsync(url, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.Networkidle2],
            Timeout = 30_000
        });
        try
        {
            await page.WaitForFunctionAsync("window.__ecpgReady === true", new WaitForFunctionOptions
            {
                Timeout = _readyTimeoutSeconds * 1_000
            });
        }
        catch (WaitTaskTimeoutException)
        {
            _logger.LogWarning("HTML-Dokument meldet nach {Sekunden} Sekunden keine Druckbereitschaft.",
                _readyTimeoutSeconds);
        }

        await page.PdfAsync(targetPath, new PdfOptions
        {
            PreferCSSPageSize = true,
            PrintBackground = true
        });
    }

    private string? FindEdge()
    {
        var candidates = new[]
        {
            _configuredPath,
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }
}
