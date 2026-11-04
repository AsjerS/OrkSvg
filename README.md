# OrkSvg

A somewhat overkill SVG to PNG renderer, using Chromium for full spec compliance

[![Release](https://img.shields.io/github/v/release/AsjerS/OrkSvg)](https://github.com/AsjerS/OrkSvg/releases)
[![NuGet](https://img.shields.io/nuget/v/OrkSvg)](https://www.nuget.org/packages/OrkSvg)
[![License](https://img.shields.io/github/license/AsjerS/OrkSvg)](LICENSE)
[![Runtime](https://img.shields.io/badge/runtime-.NET%2010.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)

## Installation

This program requires [.NET Runtime 10.0](https://dotnet.microsoft.com/download/dotnet/10.0) to run.

To install the program, run this command:

```
dotnet tool install --global OrkSvg
```

And to update/uninstall:

```
dotnet tool update --global OrkSvg     # to update
dotnet tool uninstall --global OrkSvg  # to uninstall
```

## Why?

Other SVG renderers are great and you should probably use them over this when possible, but it's not always possible; none I could find implement the full SVG spec.

This project is for those who need to render SVGs that use features that normally only browsers support, like custom CSS properties, variables, embedded fonts, and anything else other SVG renderers might not support.

So you will probably get the most perfect render with this, but at the cost of quite a bit of extra compute time.

## How?

It auto-downloads (when needed) and runs a Chromium engine via Puppeteer (or your own browser when provided), and uses that to render the SVG and save to a PNG file.

## Usage

To render an SVG:

```
orksvg logo.svg
```

To render an SVG with a custom output filename and at 2x render scale (relative to what's in the SVG's metadata):

```
orksvg logo.svg output.png -s 2
```

To also specify a custom output file path, render at 4.5x scale, and use an existing Edge installation:

```
orksvg logo.svg "C:\Users\name\Downloads\output.png" --render-scale 4.5 --browser-path "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
```

To get help with all possible options and syntax:

```
orksvg --help
```
