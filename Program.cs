using PuppeteerSharp;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace OrkSvg;

public sealed class Program
{
    public static async Task<int> Main(string[] args)
    {
        var app = new CommandApp<RenderCommand>();

        app.Configure(config =>
        {
            config.SetApplicationName("OrkSvg");
            config.SetApplicationVersion(
                Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
                    ?? "0.0.0"
            );
        });

        return await app.RunAsync(args);
    }
}

public sealed class RenderSettings : CommandSettings
{
    [Description("Path to the input SVG file")]
    [CommandArgument(0, "<INPUT>")]
    public string InputPath { get; set; } = string.Empty;

    [Description("Path to the output PNG file (optional, defaults to the same as the input with a .png extension)")]
    [CommandArgument(1, "[OUTPUT]")]
    public string? OutputPath { get; set; }

    [Description("Rasterization scale multiplier")]
    [CommandOption("-s|--render-scale <SCALE>")]
    [DefaultValue(1.0f)]
    public float RenderScale { get; init; }

    [Description("Path to browser executable (optional)")]
    [CommandOption("-b|--browser-path <PATH>")]
    public string? BrowserPath { get; set; }

    [Description("Print raw browser logs to the console for debugging")]
    [CommandOption("-v|--verbose")]
    public bool Verbose { get; init; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath))
            return ValidationResult.Error($"Input file not found: '{InputPath}'");

        InputPath = Path.GetFullPath(InputPath);

        OutputPath = string.IsNullOrWhiteSpace(OutputPath)
            ? Path.ChangeExtension(InputPath, ".png")
            : Path.GetFullPath(OutputPath);

        if (RenderScale <= 0)
            return ValidationResult.Error("RenderScale must be greater than 0");

        if (!string.IsNullOrEmpty(BrowserPath))
        {
            if (!File.Exists(BrowserPath))
                return ValidationResult.Error($"Specified browser executable not found: '{BrowserPath}'");

            BrowserPath = Path.GetFullPath(BrowserPath);
        }

        return ValidationResult.Success();
    }
}

public sealed class RenderCommand : AsyncCommand<RenderSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RenderSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var svgContent = await File.ReadAllTextAsync(settings.InputPath, cancellationToken);
            var (baseWidth, baseHeight) = SvgMetadataParser.ExtractDimensions(svgContent);

            int targetWidth = (int)Math.Round(baseWidth * settings.RenderScale);
            int targetHeight = (int)Math.Round(baseHeight * settings.RenderScale);

            await AnsiConsole
                .Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Initializing Chromium engine", async ctx =>
                {
                    if (string.IsNullOrEmpty(settings.BrowserPath))
                    {
                        var fetcher = new BrowserFetcher();
                        var installedBrowser = fetcher.GetInstalledBrowsers().FirstOrDefault();

                        if (installedBrowser is null)
                        {
                            ctx.Status("Downloading Chromium engine");
                            installedBrowser = await fetcher.DownloadAsync();
                        }

                        settings.BrowserPath = installedBrowser!.GetExecutablePath();
                    }

                    ctx.Status("Launching browser");
                    using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
                    {
                        Headless = true,
                        ExecutablePath = settings.BrowserPath,
                        Pipe = true,
                        DumpIO = settings.Verbose,
                        Args =
                        [
                            "--default-background-color=00000000",
                            "--disable-extensions",
                            "--disable-component-update"
                        ]
                    });

                    ctx.Status(
                        $"Rendering at [yellow]{settings.RenderScale}x[/] scale ({targetWidth}x{targetHeight}px)"
                    );

                    using var page = await browser.NewPageAsync();

                    await page.SetViewportAsync(new ViewPortOptions
                    {
                        Width = (int)Math.Ceiling(baseWidth),
                        Height = (int)Math.Ceiling(baseHeight),
                        DeviceScaleFactor = settings.RenderScale
                    });

                    await page.SetContentAsync($$"""
                        <!DOCTYPE html>
                        <html>
                        <head>
                            <style>
                                * { margin: 0; padding: 0; overflow: hidden; background: transparent; }
                                svg { display: block; width: 100vw; height: 100vh; }
                            </style>
                        </head>
                        <body>
                            {{svgContent}}
                        </body>
                        </html>
                        """
                    );

                    ctx.Status("Exporting PNG");
                    await page.ScreenshotAsync(settings.OutputPath, new ScreenshotOptions
                    {
                        OmitBackground = true,
                        Type = ScreenshotType.Png
                    });
                });

            AnsiConsole.MarkupLine("[green]Render completed successfully[/]");
            AnsiConsole.MarkupLine($"[grey]Output: {settings.OutputPath}[/]");

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }
}

internal static partial class SvgMetadataParser
{
    [GeneratedRegex(
        @"viewBox\s*=\s*[""']\s*[-+]?[\d.]+[,\s]+[-+]?[\d.]+[,\s]+([-+]?[\d.]+)[,\s]+([-+]?[\d.]+)\s*[""']",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex ViewBoxRegex();

    [GeneratedRegex(
        @"\bwidth\s*=\s*[""']([\d.]+)p?x?[""']",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex WidthRegex();

    [GeneratedRegex(
        @"\bheight\s*=\s*[""']([\d.]+)p?x?[""']",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex HeightRegex();

    public sealed class SvgParseException(string message) : Exception(message);

    public static (double Width, double Height) ExtractDimensions(string svg)
    {
        // try viewbox
        var viewBoxMatch = ViewBoxRegex().Match(svg);

        if (
            viewBoxMatch.Success
            && double.TryParse(
                viewBoxMatch.Groups[1].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double vbW
            )
            && double.TryParse(
                viewBoxMatch.Groups[2].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double vbH
            )
        ) return (vbW, vbH);

        // fall back to width/height attributes
        var wMatch = WidthRegex().Match(svg);
        var hMatch = HeightRegex().Match(svg);

        if (
            wMatch.Success
            && hMatch.Success
            && double.TryParse(
                wMatch.Groups[1].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double w
            )
            && double.TryParse(
                hMatch.Groups[1].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double h
            )
        ) return (w, h);

        throw new SvgParseException("Could not find SVG size");
    }
}
