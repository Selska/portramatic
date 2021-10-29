﻿// See https://aka.ms/new-console-template for more information

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Skia;
using HtmlAgilityPack;
using Portramatic.DTOs;
using Portramatic.ViewModels;
using ReactiveUI;
using Shipwreck.Phash;
using SkiaSharp;
using ThumbnailGenerator;

class Program
{
    private static HttpClient Client = new();

    private static TimeSpan WaitTime = TimeSpan.FromSeconds(1);
    private static Stopwatch QueryTimer = new();

    public static async Task<int> Main(string[] args)
    {
        QueryTimer.Restart();
        
        Client.DefaultRequestHeaders.Add("User-Agent","Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/94.0.4606.81 Safari/537.36");
        
        var files = Directory.EnumerateFiles(Path.Combine(args[0], "Definitions"), "definition.json",
                SearchOption.AllDirectories)
            .ToArray();

        Console.WriteLine($"Found {files.Length} definitions, loading...");

        var definitions = new List<(PortraitDefinition, string)>();

        foreach (var file in files)
        {
            definitions.Add((JsonSerializer.Deserialize<PortraitDefinition>(await File.ReadAllTextAsync(file),
                new JsonSerializerOptions()
                {
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
                })!, file));
        }

        Console.WriteLine($"Loaded {definitions.Count} definitions, creating gallery files");

        var pOptions = new ParallelOptions() {MaxDegreeOfParallelism = 32};

        var outputMemoryStream = new MemoryStream();
        {
            using var archive = new ZipArchive(outputMemoryStream, ZipArchiveMode.Update, true);
            {
                var badLinks = new ConcurrentBag<string>();
                var hashes =
                    new ConcurrentBag<(PortraitDefinition Definition, Digest Hash, Size Size, string Filename)>();
                
                await Parallel.ForEachAsync(definitions.Select((v, idx) => (v.Item1, v.Item2,  idx)),
                    pOptions, 
                    async (itm, token) =>
                {
                    var (definition, path, idx) = itm;
                    if (!definition.Requeried) 
                        definition.Tags = await GetLabels(definition.Source);
                    Console.WriteLine(
                        $"[{idx}/{definitions.Count}]Adding {definition.Source.ToString().Substring(0, Math.Min(70, definition.Source.ToString().Length))}");
                    try
                    {
                        var (hash, size) = await GenerateThumbnail(archive, definition);
                        hashes.Add((definition, hash, size, path));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"BAD: {definition.MD5}");
                        File.Delete(path);
                        badLinks.Add(definition.MD5);
                    }
                });
                
                Console.WriteLine("Finding duplicate images");

                var grouped = from src in hashes.AsParallel()
                    from dest in hashes
                    where src.Definition.MD5 != dest.Definition.MD5
                    let cross = ImagePhash.GetCrossCorrelation(src.Hash, dest.Hash)
                    where cross >= 0.99f
                    where src.Size.Width * src.Size.Height >= dest.Size.Width * dest.Size.Height
                    group (dest, cross) by src.Definition.MD5 into result
                    select result;
                
                var results = grouped.ToArray();
                
                Console.WriteLine($"Found {results.Length} duplicate pairs");
                foreach (var result in results)
                {
                    foreach (var (dest, _) in result)
                    {
                        Console.WriteLine($"Removing {dest.Definition.MD5}");
                        badLinks.Add(dest.Definition.MD5);
                        if (File.Exists(dest.Filename))
                            File.Delete(dest.Filename);
                        archive.GetEntry(dest.Definition.MD5+".webp")?.Delete();
                    }
                }
                
                definitions = definitions.Where(d => !badLinks.Contains(d.Item1.MD5)).ToList();
                {
                    var tocEntry = archive.CreateEntry("definitions.json", CompressionLevel.SmallestSize);
                    await using var tocStream = tocEntry.Open();
                    await using var sw = new StreamWriter(tocStream);
                    var json = JsonSerializer.Serialize(definitions.Select(d => d.Item1).ToArray(), new JsonSerializerOptions()
                    {
                        WriteIndented = false,
                        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
                    });
                    await sw.WriteAsync(json);
                }
            }
        }
        Console.WriteLine($"Output zip is {outputMemoryStream.Length} bytes in size");
        await File.WriteAllBytesAsync(Path.Combine(args[1], "gallery.zip"), outputMemoryStream.ToArray());
        return 0;
    }

    private static async Task<(Digest Hash, Size)> GenerateThumbnail(ZipArchive archive, PortraitDefinition definition)
    {
        var bitmapBytes = await Client.GetByteArrayAsync(definition.Source);
        var (width, height) = definition.Full.FinalSize;
        using var src = SKImage.FromEncodedData(new MemoryStream(bitmapBytes));

        
        var ibitmap = new SKImageIBitmap(src);
        var hash = ImagePhash.ComputeDigest(ibitmap);

        var cropped = definition.Crop(src, ImageSize.Full);
        var snap = Resize(cropped, width / 4, height / 4);

        var encoded = snap.Encode(SKEncodedImageFormat.Webp, 50);

        if (string.IsNullOrEmpty(definition.MD5))
            throw new InvalidDataException("MD5");

        lock (archive)
        {
            var entry = archive.CreateEntry(definition.MD5 + ".webp");
            using var entryStream = entry.Open();
            encoded.SaveTo(entryStream);
        }

        return (hash, new Size(src.Width, src.Height));
    }

    private static SKImage Resize(SKImage i, int width, int height)
    {
        var paint = new SKPaint();
        paint.FilterQuality = SKFilterQuality.High;
        
        using var surface =
            SKSurface.Create(new SKImageInfo(i.Width / 4, i.Height / 4, SKColorType.Bgra8888, SKAlphaType.Opaque));
        surface.Canvas.DrawImage(i, new SKRect(0, 0, i.Width, i.Height),
            new SKRect(0, 0, (float)i.Width / 4, (float)i.Height / 4),
            paint);
        return surface.Snapshot();
    }

    
    private static HashSet<string> FindInSites = new HashSet<string>()
    {
        "deviantart",
        "pintrest",
        "artstation"
    };

    private static HashSet<string> FilterWords = new HashSet<string>()
    {
        "on", "and", "in", "the", "on", "of", "after",
        "artstation",
        "-",
        "pintrest",
        "deviantart",
        "by",
        "art",
        "dnd",
        "|",
        "/",
        "..."
    };

    private static char[] TrimChars = {',', ';', '+', ' '};

    private static Random RNG = new();
    private static async Task<string[]> GetLabels(Uri source)
    {
        TOP:

        var googleResponse =
                await Client.GetStringAsync(
                    $"https://www.google.com/searchbyimage?image_url={HttpUtility.UrlEncode(source.ToString())}");

        if (!googleResponse.Contains("Possible related search:"))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(RNG.Next(500, 3000)));
            goto TOP;
        }
        
            var doc = new HtmlDocument();
            doc.LoadHtml(googleResponse);

            var results = doc.DocumentNode.Descendants()
                .Where(d => d.InnerText.StartsWith("Possible related search:"))
                .Where(n => n.Name == "div")
                .SelectMany(d => d.SelectNodes("a").Select(n => n.InnerText))
                .ToList();



            var resultsQuery = from desc in doc.DocumentNode.Descendants()
                where desc.Name == "a"
                where desc.Descendants().Any(de => de.Name == "h3")
                let anc = desc.GetAttributeValue("href", "")
                where FindInSites.Any(anc.Contains)
                select desc.Descendants().First(de => de.Name == "h3").InnerText;

            var resultsTitles = resultsQuery.ToList();

            var alltags = resultsTitles.Concat(results)
                .SelectMany(s => s.Split(" ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Select(s => s.ToLower().Trim(TrimChars))
                .Distinct()
                .Where(t => !FilterWords.Contains(t))
                .ToArray();


            Console.WriteLine($"Tags: {string.Join(", ", alltags)}");
            return alltags;
        
    }
}