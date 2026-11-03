using Microsoft.Win32;
using PuppeteerSharp;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using static System.Environment;

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
                    ?? "unknown"
            );
        });

        return await app.RunAsync(args);
    }
}

public sealed class RenderSettings : CommandSettings
{
    [Description("Path to the input SVG file.")]
    [CommandArgument(0, "<INPUT>")]
    public string InputPath { get; set; } = string.Empty;

    [Description("Path to the output PNG file. (Defaults to same name with .png extension)")]
    [CommandArgument(1, "[OUTPUT]")]
    public string? OutputPath { get; set; }

    [Description("Rasterization scale multiplier.")]
    [CommandOption("-s|--render-scale <SCALE>")]
    [DefaultValue(1.0f)]
    public float RenderScale { get; init; }

    [Description("Path to browser executable.")]
    [CommandOption("-b|--browser-path <PATH>")]
    public string? BrowserPath { get; set; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath))
            return ValidationResult.Error($"Input file not found: '{InputPath}'");

        InputPath = Path.GetFullPath(InputPath);

        OutputPath = string.IsNullOrWhiteSpace(OutputPath)
            ? Path.ChangeExtension(InputPath, ".png")
            : Path.GetFullPath(OutputPath);

        if (RenderScale <= 0)
            return ValidationResult.Error("RenderScale must be greater than 0.");

        BrowserPath ??= BrowserLocator.FindSystemBrowser();

        if (string.IsNullOrEmpty(BrowserPath) || !File.Exists(BrowserPath))
            return ValidationResult.Error("Could not find a valid browser on this machine.");

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
                .StartAsync("Initializing Chromium engine...", async ctx =>
                {
                    ctx.Status("Launching browser...");
                    using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
                    {
                        Headless = true,
                        ExecutablePath = settings.BrowserPath,
                        Args =
                        [
                            "--default-background-color=00000000",
                            "--no-sandbox",
                            "--disable-gpu",
                            "--disable-extensions"
                        ]
                    });

                    ctx.Status(
                        $"Rasterizing at [yellow]{settings.RenderScale}x[/] scale ({targetWidth}x{targetHeight}px)..."
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

                    ctx.Status("Exporting PNG...");
                    await page.ScreenshotAsync(settings.OutputPath, new ScreenshotOptions
                    {
                        OmitBackground = true,
                        Type = ScreenshotType.Png
                    });
                });

            AnsiConsole.MarkupLine("[green]Render completed successfully![/]");
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("[grey]Property[/]");
            table.AddColumn("[grey]Value[/]");
            table.AddRow("Output", $"[bold]{settings.OutputPath}[/]");
            table.AddRow("Resolution", $"{targetWidth} × {targetHeight} px");

            AnsiConsole.Write(table);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Fatal Error:[/] {ex.Message}");
            return 1;
        }
    }
}

internal static partial class SvgMetadataParser
{
    public sealed class SvgParseException(string message) : Exception(message);

    public static (double Width, double Height) ExtractDimensions(string svg)
    {
        // try viewbox
        var viewBoxMatch = Regex.Match(
            svg,
            @"viewBox\s*=\s*[""']\s*[-+]?[\d.]+[,\s]+[-+]?[\d.]+[,\s]+([-+]?[\d.]+)[,\s]+([-+]?[\d.]+)\s*[""']",
            RegexOptions.IgnoreCase);

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
        var wMatch = Regex.Match(
            svg,
            @"\bwidth\s*=\s*[""']([\d.]+)p?x?[""']",
            RegexOptions.IgnoreCase
        );

        var hMatch = Regex.Match(
            svg,
            @"\bheight\s*=\s*[""']([\d.]+)p?x?[""']",
            RegexOptions.IgnoreCase
        );

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

internal static class BrowserLocator
{
    public static string? FindSystemBrowser()
    {
        if (OperatingSystem.IsWindows())
            return FindWindowsBrowser();

        if (OperatingSystem.IsMacOS())
            return FindFirstExisting(
                [
                    "Google Chrome.app/Contents/MacOS/Google Chrome",
                    "Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                    "Brave Browser.app/Contents/MacOS/Brave Browser",
                    "Chromium.app/Contents/MacOS/Chromium"
                ],
                [
                    "/Applications",
                    Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.UserProfile
                        ),
                        "Applications"
                    )
                ]
            );

        if (OperatingSystem.IsLinux())
            return FindFirstExisting(
                [
                    "google-chrome",
                    "google-chrome-stable",
                    "chromium",
                    "chromium-browser",
                    "microsoft-edge-stable",
                    "brave-browser"
                ],
                (Environment.GetEnvironmentVariable("PATH") ?? "")
                    .Split(
                        Path.PathSeparator,
                        StringSplitOptions.RemoveEmptyEntries
                    )
            );

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? FindWindowsBrowser()
    {
        var path = ((string[])[
            "chrome.exe",
            "brave.exe",
            "msedge.exe"
        ])
            .Select(exe =>
                Registry.GetValue(
                    $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}",
                    "",
                    null
                ) as string
                    ?? Registry.GetValue(
                        $@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}",
                        "",
                        null
                    ) as string)
            .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));

        if (path is not null) return path;

        foreach (var root in ((string[])[
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFilesX86
                ),
                @"Microsoft\EdgeWebView\Application"
            ),
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles
                ),
                @"Microsoft\EdgeWebView\Application"
        )
        ]))
        {
            if (Directory.Exists(root))
            {
                var webViewExe = Directory
                    .EnumerateFiles(
                        root,
                        "msedgewebview2.exe",
                        SearchOption.AllDirectories
                    )
                    .FirstOrDefault();

                if (webViewExe is not null) return webViewExe;
            }
        }

        return null;
    }

    private static string? FindFirstExisting(IEnumerable<string> files, IEnumerable<string> directories) =>
        files.SelectMany(file => directories.Select(dir => Path.Combine(dir, file))).FirstOrDefault(File.Exists);
}
